"""Write public SPA settings into a published Blazor wwwroot (no credentials)."""
import json
import os
from pathlib import Path
import sys
from urllib.parse import urlsplit
import uuid


def main():
    root = Path(sys.argv[1])
    gateway = os.environ["MONITORING_GATEWAY_URL"].rstrip("/")
    parsed = urlsplit(gateway)
    if parsed.scheme != "https" or not parsed.hostname or parsed.path or parsed.query or parsed.fragment or parsed.username:
        raise SystemExit("MONITORING_GATEWAY_URL must be an HTTPS origin.")
    tenant = str(uuid.UUID(os.environ["AZURE_TENANT_ID"]))
    client = str(uuid.UUID(os.environ["MONITORING_UI_CLIENT_ID"]))
    gateway_client = str(uuid.UUID(os.environ["GATEWAY_AUTH_CLIENT_ID"]))
    config = {
        "Monitoring": {
            "TenantId": tenant,
            "ClientId": client,
            "GatewayBaseUrl": gateway,
            "GatewayScope": f"api://{gateway_client}/monitoring.admin",
            "UseDevelopmentAuthentication": False,
        }
    }
    (root / "appsettings.json").write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
    # Never publish local bypass settings. Blazor can select an environment via a
    # hosting header, so these must also be absent from the production artifact.
    for development_file in root.glob("appsettings.Development.json*"):
        development_file.unlink()
    # These settings are written after publish. Remove stale precompressed copies
    # so a static host cannot serve the placeholder configuration instead.
    for suffix in (".br", ".gz"):
        (root / ("appsettings.json" + suffix)).unlink(missing_ok=True)


if __name__ == "__main__":
    main()
