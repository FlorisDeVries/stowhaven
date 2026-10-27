#!/usr/bin/env python3
"""Run real client/API/worker recovery and restore against isolated Azurite + Dapr.

Requires .NET 10, Docker, and Linux host networking. All configuration and data are
synthetic. No Azure account, repository .env, or existing containers are used.
"""
import argparse
import base64
import hashlib
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.request import Request, urlopen
import uuid


REPO = Path(__file__).resolve().parents[1]
ACCOUNT = "stowhaventest"
KEY = base64.b64encode(b"synthetic-recovery-test-key-only!").decode()


def port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def request(url, body=None):
    data = None if body is None else json.dumps(body).encode()
    with urlopen(Request(url, data=data, headers={"Content-Type": "application/json"}), timeout=10) as response:
        content = response.read()
        return json.loads(content) if content else None


def until(predicate, description, timeout=90):
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        try:
            value = predicate()
            if value:
                return value
        except (OSError, ValueError) as error:
            last_error = error
        time.sleep(0.1)
    raise RuntimeError(f"Timed out waiting for {description}; last error: {type(last_error).__name__}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, help="Directory for isolated test data and logs")
    args = parser.parse_args()
    root = (args.artifacts or Path(tempfile.mkdtemp(prefix="stowhaven-recovery-"))).resolve()
    if root.exists() and (not root.is_dir() or any(root.iterdir())):
        parser.error("--artifacts must name a new or empty directory")
    root.mkdir(parents=True, exist_ok=True)
    for name in ("config", "data", "logs", "components", "source"):
        (root / name).mkdir(exist_ok=True)
    tag = "stowhaven-e2e-" + uuid.uuid4().hex[:10]
    children, containers, handles = [], [], []
    blob_port, queue_port, api_port, worker_port = (port() for _ in range(4))
    api_http, api_grpc, worker_http, worker_grpc = (port() for _ in range(4))
    api_url = f"http://127.0.0.1:{api_port}"
    excluded = ("BACKUP", "DATABASE", "AZURE", "DAPR", "DATA_", "USE_AZURITE", "OTEL", "SERILOG", "APPLICATIONINSIGHTS")
    environment = {key: value for key, value in os.environ.items() if not key.upper().startswith(excluded)}
    environment.update({
        "DOTNET_ENVIRONMENT": "Development", "ASPNETCORE_ENVIRONMENT": "Development",
        "ALLOW_DEVELOPMENT_ANONYMOUS_AUTHENTICATION": "true",
        "XDG_CONFIG_HOME": str(root / "config"), "XDG_DATA_HOME": str(root / "data"),
        "NO_PROXY": "localhost,127.0.0.1", "USE_AZURITE": "true", "DATA_STORAGE_ACCOUNT": ACCOUNT,
        "DATA_STORAGE_ACCOUNT_KEY": KEY, "DATA_CONTAINER": "backups",
        "DATA_STORAGE_BLOB_ENDPOINT": f"http://127.0.0.1:{blob_port}/{ACCOUNT}",
        "Database__Provider": "Sqlite", "Database__Sqlite__FilePath": str(root / "server.db"),
        "CommitProcessing__LeaseDurationSeconds": "5", "CommitProcessing__HeartbeatIntervalSeconds": "1",
        "CommitProcessing__MaxParallelFiles": "1", "LOG_PATH": str(root / "logs"),
        "OTEL_EXPORTER_OTLP_ENDPOINT": "", "OTEL_EXPORTER_AZURE_MONITOR_CONNECTION": "",
    })

    def run(command, name, env=None):
        with (root / "logs" / f"{name}.log").open("w") as output:
            result = subprocess.run(command, cwd=REPO, env=env or environment, stdout=output, stderr=subprocess.STDOUT)
        if result.returncode:
            raise RuntimeError(f"{name} exited {result.returncode}; see its log in {root / 'logs'}")

    def start(command, name, env=None, cwd=REPO):
        output = (root / "logs" / f"{name}.log").open("w")
        handles.append(output)
        process = subprocess.Popen(command, cwd=cwd, env=env or environment, stdout=output, stderr=subprocess.STDOUT)
        children.append(process)
        return process

    def docker(arguments, name):
        container = f"{tag}-{name}"
        containers.append(container)
        run(["docker", "run", "-d", "--name", container, *arguments], name)

    def server_env(http_port, grpc_port, app_port):
        return {**environment, "ASPNETCORE_URLS": f"http://127.0.0.1:{app_port}",
            "DAPR_HTTP_PORT": str(http_port), "DAPR_GRPC_PORT": str(grpc_port),
            "DAPR_HTTP_ENDPOINT": f"http://127.0.0.1:{http_port}",
            "DAPR_GRPC_ENDPOINT": f"http://127.0.0.1:{grpc_port}"}

    def sidecar(app_id, app_port, http_port, grpc_port):
        docker(["--network", "host", "--entrypoint", "/daprd", "-v", f"{root / 'components'}:/components:ro",
            "daprio/daprd:1.13.6", "--app-id", app_id, "--app-port", str(app_port),
            "--app-channel-address", "127.0.0.1", "--dapr-listen-addresses", "127.0.0.1",
            "--dapr-http-port", str(http_port), "--dapr-grpc-port", str(grpc_port),
            "--dapr-internal-grpc-port", str(port()), "--metrics-port", str(port()),
            "--resources-path", "/components", "--log-level", "warn"], app_id + "-dapr")
        until(lambda: request(f"http://127.0.0.1:{http_port}/v1.0/healthz/outbound") is None, app_id + " sidecar")

    proxy = None
    try:
        print("Publishing the real API, worker, and client...", flush=True)
        projects = {"api": "FlorisDeV.BackupApi", "worker": "FlorisDeV.BackupWorker", "client": "FlorisDeV.BackupClient"}
        for service, assembly in projects.items():
            # A unique test-only metadata identifier prevents Development mode from loading
            # a developer's real user-secret settings (including additional backup targets).
            run(["dotnet", "publish", f"src/services/{service}/{assembly}.csproj", "-c", "Release",
                "-p:NuGetAudit=false", f"-p:UserSecretsId={tag}", "--ignore-failed-sources", "-o", str(root / service)], "publish-" + service)

        docker(["-e", f"AZURITE_ACCOUNTS={ACCOUNT}:{KEY}", "-p", f"127.0.0.1:{blob_port}:10000",
            "-p", f"127.0.0.1:{queue_port}:10001", "mcr.microsoft.com/azure-storage/azurite:latest",
            "azurite", "--blobHost", "0.0.0.0", "--queueHost", "0.0.0.0", "--skipApiVersionCheck", "--silent"], "azurite")
        for direction, scope in (("output", "backup-api"), ("input", "backup-worker")):
            component = {"apiVersion": "dapr.io/v1alpha1", "kind": "Component",
                "metadata": {"name": "backup-events-" + direction}, "spec": {
                    "type": "bindings.azure.storagequeues", "version": "v1", "metadata": [
                        {"name": key, "value": value} for key, value in {
                            "accountName": ACCOUNT, "accountKey": KEY, "queueName": "backup-events",
                            "endpoint": f"http://127.0.0.1:{queue_port}", "direction": direction,
                            "route": "/api/backupevents/backup-run-committed", "pollingInterval": "1s", "visibilityTimeout": "5s"
                        }.items()]}, "scopes": [scope]}
            (root / "components" / f"events-{direction}.yaml").write_text(json.dumps(component))

        start(["dotnet", str(root / "api" / "FlorisDeV.BackupApi.dll")], "api", server_env(api_http, api_grpc, api_port), root / "api")
        until(lambda: request(api_url + "/health/liveness"), "API")
        sidecar("backup-api", api_port, api_http, api_grpc)

        class Proxy(BaseHTTPRequestHandler):
            drop_next_commit = False
            dropped_commit = None

            def log_message(self, *_):
                pass

            def forward(self):
                connection = http.client.HTTPConnection("127.0.0.1", api_port, timeout=60)
                if "chunked" in self.headers.get("Transfer-Encoding", "").lower():
                    chunks = []
                    while True:
                        length = int(self.rfile.readline().split(b";", 1)[0].strip(), 16)
                        if length == 0:
                            while self.rfile.readline().strip():
                                pass
                            break
                        chunks.append(self.rfile.read(length))
                        self.rfile.read(2)
                    body = b"".join(chunks)
                else:
                    body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
                headers = {key: value for key, value in self.headers.items() if key.lower() not in ("host", "connection", "transfer-encoding", "content-length")}
                connection.request(self.command, self.path, body, headers)
                response = connection.getresponse()
                content = response.read()
                if response.status >= 400:
                    (root / "logs" / "last-http-error.json").write_text(json.dumps({
                        "path": self.path, "status": response.status, "response": content.decode(errors="replace")}, indent=2))
                if self.path.endswith("/backup/commit-run") and Proxy.drop_next_commit and response.status == 202:
                    Proxy.drop_next_commit = False
                    committed = json.loads(content)
                    Proxy.dropped_commit = committed["commitId"]
                    status_url = api_url + f"/api/devices/{committed['deviceId']}/backup/commit-status/{committed['commitId']}"
                    until(lambda: request(status_url)["status"] == "succeeded", "commit before dropping its response")
                    self.close_connection = True
                    self.connection.shutdown(socket.SHUT_RDWR)
                    connection.close()
                    return
                self.send_response(response.status)
                for key, value in response.getheaders():
                    if key.lower() not in ("transfer-encoding", "connection", "content-length"):
                        self.send_header(key, value)
                self.send_header("Content-Length", str(len(content)))
                self.end_headers()
                self.wfile.write(content)
                connection.close()

            do_GET = forward
            do_POST = forward

        proxy = ThreadingHTTPServer(("127.0.0.1", 0), Proxy)
        threading.Thread(target=proxy.serve_forever, daemon=True).start()
        settings = {
            "BackupClient": {"BackupTargets": {"fixture": str(root / "source")}, "IgnoreFilePath": "",
                "CommitStatusTimeoutSeconds": 0, "CommitStatusPollIntervalSeconds": 1, "MaxParallelUploads": 4,
                "Encryption": {"Mode": "ClientAndServer", "KdfIterations": 1000, "RecoveryPhraseFilePath": str(root / "phrase.json")},
                "Schedule": {"Enabled": False}},
            "BackupApiClient": {"ApiUrl": f"http://127.0.0.1:{proxy.server_port}", "AuthenticationScope": "local-test",
                "AuthenticationTenant": "local-test", "WakeUp": {"Enabled": False},
                "RetryOptions": {"Retry": {"MaxRetryAttempts": 3, "Delay": "00:00:00.100", "BackoffType": "Constant"}}},
            "AzureAd": {"TenantId": "local-test", "ClientId": "local-test"},
            "Database": {"FilePath": str(root / "client.db")}, "OTEL_EXPORTER_OTLP_ENDPOINT": ""
        }
        local_config = root / "client" / "appsettings.local.json"

        def client(name, command="backup", accepted=(0,)):
            local_config.write_text(json.dumps(settings))
            result_path = root / f"{name}-result.json"
            with (root / "logs" / f"{name}.log").open("w") as output:
                process = subprocess.run(["dotnet", str(root / "client" / "FlorisDeV.BackupClient.dll"), command,
                    "--result-file", str(result_path)], cwd=root, env=environment, stdout=output, stderr=subprocess.STDOUT, timeout=120)
            if process.returncode not in accepted:
                raise RuntimeError(f"{name} exited {process.returncode}; see {root / 'logs' / (name + '.log')}")
            return json.loads(result_path.read_text())

        for index in range(200):
            (root / "source" / f"file-{index:03}.txt").write_text(f"Recovery fixture content {index}\n" * 20)
        print("Submitting an encrypted backup while the worker is stopped...", flush=True)
        submitted = client("submitted", accepted=(2,))
        assert submitted["outcome"] == "pending", submitted
        assert submitted["filesScanned"] == 200, "The test must scan only its isolated source tree."
        status_url = api_url + f"/api/devices/{submitted['deviceId']}/backup/commit-status/{submitted['commitId']}"
        worker_command = ["dotnet", str(root / "worker" / "FlorisDeV.BackupWorker.dll")]
        worker_environment = server_env(worker_http, worker_grpc, worker_port)
        worker = start(worker_command, "worker-first", worker_environment, root / "worker")
        until(lambda: request(f"http://127.0.0.1:{worker_port}/health/liveness"), "worker")
        sidecar("backup-worker", worker_port, worker_http, worker_grpc)
        until(lambda: request(status_url)["status"] == "processing", "worker claim")
        worker.kill()
        worker.wait(timeout=10)
        print("Worker terminated mid-commit; restarting and waiting for lease recovery...", flush=True)
        start(worker_command, "worker-restarted", worker_environment, root / "worker")
        completed = until(lambda: (state if (state := request(status_url))["status"] == "succeeded" else None), "recovered commit", timeout=90)
        assert completed["attemptCount"] >= 2, completed
        finalized = client("finalized")
        assert finalized["commitId"] == submitted["commitId"], finalized

        (root / "source" / "file-000.txt").write_text("Updated content after recovery\n")
        (root / "source" / "file-001.txt").unlink()
        (root / "source" / "new-file.txt").write_text("A new file in the second run\n")
        settings["BackupClient"]["CommitStatusTimeoutSeconds"] = 60
        Proxy.drop_next_commit = True
        print("Dropping a successful commit response and retrying the same run...", flush=True)
        retried = client("lost-response")
        assert Proxy.dropped_commit and retried["commitId"] == Proxy.dropped_commit, retried
        assert retried["outcome"] == "succeeded", retried

        settings["BackupClient"]["Restore"] = {"DeviceId": submitted["deviceId"], "DestinationPath": str(root / "restored"), "LogicalPaths": []}
        print("Restoring and verifying every plaintext SHA-256...", flush=True)
        client("restore", "restore")
        expected = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in (root / "source").iterdir()}
        actual = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in (root / "restored" / "fixture").iterdir()}
        assert expected == actual, "Restored contents or deletion results differ from the current source tree."
        summary = {"passed": True, "restoredFiles": len(actual), "recoveredAttempts": completed["attemptCount"],
            "checks": ["pending outcome", "worker termination and restart", "expired lease recovery", "lost commit response",
                "incremental update", "deletion", "encrypted restore", "plaintext SHA-256 verification"]}
        (root / "summary.json").write_text(json.dumps(summary, indent=2))
        print(json.dumps(summary), flush=True)
    finally:
        if proxy:
            proxy.shutdown()
            proxy.server_close()
        for process in reversed(children):
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)
        for container in reversed(containers):
            with (root / "logs" / f"{container}.log").open("w") as output:
                subprocess.run(["docker", "logs", container], stdout=output, stderr=subprocess.STDOUT)
            subprocess.run(["docker", "rm", "-f", container], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for handle in handles:
            handle.close()
        print(f"Recovery test artifacts: {root}", flush=True)


if __name__ == "__main__":
    main()
