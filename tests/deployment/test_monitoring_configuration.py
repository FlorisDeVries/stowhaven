import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "scripts/configure-monitoring-ui.py"


class MonitoringConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.environment = dict(os.environ, **{
            "MONITORING_GATEWAY_URL": "https://gateway.example.com/",
            "AZURE_TENANT_ID": "11111111-1111-1111-1111-111111111111",
            "MONITORING_UI_CLIENT_ID": "22222222-2222-2222-2222-222222222222",
            "GATEWAY_AUTH_CLIENT_ID": "33333333-3333-3333-3333-333333333333",
        })

    def run_script(self):
        return subprocess.run([sys.executable, str(SCRIPT), str(self.root)], env=self.environment,
                              capture_output=True, text=True)

    def test_production_artifact_has_public_settings_and_no_local_bypass_or_stale_compression(self):
        for name in ("appsettings.Development.json", "appsettings.Development.json.br",
                     "appsettings.Development.json.gz", "appsettings.json.br", "appsettings.json.gz"):
            (self.root / name).write_text("stale settings")
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stderr)
        config = json.loads((self.root / "appsettings.json").read_text())["Monitoring"]
        self.assertFalse(config["UseDevelopmentAuthentication"])
        self.assertEqual(config["GatewayBaseUrl"], "https://gateway.example.com")
        self.assertEqual(config["GatewayScope"], "api://33333333-3333-3333-3333-333333333333/monitoring.admin")
        self.assertEqual([p.name for p in self.root.iterdir()], ["appsettings.json"])

    def test_rejects_http_gateway_before_writing_settings(self):
        self.environment["MONITORING_GATEWAY_URL"] = "http://gateway.example.com"
        self.assertNotEqual(self.run_script().returncode, 0)
        self.assertFalse((self.root / "appsettings.json").exists())

    def test_rejects_gateway_urls_with_credentials_or_query_parameters(self):
        for url in ("https://user:secret@gateway.example.com", "https://gateway.example.com?token=secret"):
            with self.subTest(url=url):
                self.environment["MONITORING_GATEWAY_URL"] = url
                self.assertNotEqual(self.run_script().returncode, 0)
                self.assertFalse((self.root / "appsettings.json").exists())


if __name__ == "__main__":
    unittest.main()
