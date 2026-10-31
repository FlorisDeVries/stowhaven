import json
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


INSTALLER = Path(__file__).resolve().parents[2] / "src/services/client/install.sh"


class ClientInstallationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="stowhaven-installer-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.publish = self.root / "publish"
        self.publish.mkdir()
        shutil.copy(INSTALLER, self.publish / "install.sh")
        executable = self.publish / "FlorisDeV.BackupClient"
        executable.write_text('#!/bin/sh\nprintf "%s\\n" "$*" >> "$CLIENT_CALLS"\n')
        executable.chmod(0o755)
        assembly = self.publish / "FlorisDeV.BackupClient.dll"
        assembly.write_bytes(b"synthetic assembly supporting observer commands")
        self.write_contract()
        self.commands = self.root / "commands"
        self.commands.mkdir()
        systemctl = self.commands / "systemctl"
        systemctl.write_text('#!/bin/sh\nprintf "%s\\n" "$*" >> "$SYSTEMCTL_CALLS"\n')
        systemctl.chmod(0o755)
        self.install = self.root / "installed"
        self.install.mkdir(mode=0o700)
        self.config = self.install / "appsettings.local.json"
        self.config.write_text(json.dumps({"BackupClient": {"Dashboard": {"Enabled": True}}}))
        self.config.chmod(0o600)
        self.units = self.root / "units"
        self.units.mkdir()
        for suffix in ("service", "timer"):
            (self.units / f"test-backup.{suffix}").write_text("existing backup " + suffix)
        self.environment = {"PATH": str(self.commands) + ":" + os.environ["PATH"],
                            "HOME": str(self.root / "home"),
                            "CLIENT_CALLS": str(self.root / "client-calls"),
                            "SYSTEMCTL_CALLS": str(self.root / "systemctl-calls"),
                            "BACKUP_CLIENT_INSTALL_DIR": str(self.install),
                            "BACKUP_CLIENT_BIN_DIR": str(self.root / "bin"),
                            "BACKUP_CLIENT_SYSTEMD_USER_DIR": str(self.units),
                            "BACKUP_CLIENT_UNIT_PREFIX": "test-backup"}

    def write_contract(self, single_file=False):
        names = ["FlorisDeV.BackupClient"]
        if not single_file:
            names.append("FlorisDeV.BackupClient.dll")
        (self.publish / "dashboard-contract.json").write_text(json.dumps({
            "reportingSchemaVersion": 1, "backupStateSchemaVersion": 3,
            "layout": "singleFile" if single_file else "assemblies",
            "files": {name: hashlib.sha256((self.publish / name).read_bytes()).hexdigest() for name in names}}))

    def run_installer(self, *arguments):
        return subprocess.run(["bash", str(self.publish / "install.sh"), *arguments],
                              env=self.environment, capture_output=True, text=True)

    def test_companion_only_install_preserves_config_backup_units_and_state(self):
        state = self.install / "state.db"
        state.write_bytes(b"pending backup state")
        config = self.config.read_bytes()
        result = self.run_installer("--dashboard-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse((self.root / "client-calls").exists(), "dashboard install must not configure/login")
        self.assertEqual(self.config.read_bytes(), config)
        self.assertEqual(state.read_bytes(), b"pending backup state")
        for suffix in ("service", "timer"):
            self.assertEqual((self.units / f"test-backup.{suffix}").read_text(), "existing backup " + suffix)
        unit = (self.units / "test-backup-dashboard.service").read_text()
        self.assertIn("Type=simple", unit)
        self.assertIn(" dashboard", unit)
        calls = (self.root / "systemctl-calls").read_text()
        self.assertIn("enable --now", calls)
        self.assertNotIn("test-backup.timer", calls)
        self.assertNotIn("test-backup.service", calls)
        opener = self.root / "bin/test-backup-open-dashboard"
        opened = subprocess.run([str(opener), "--print-url"], env=self.environment)
        self.assertEqual(opened.returncode, 0)
        self.assertIn("open-dashboard --BackupClient:Dashboard:BackupServiceUnit=test-backup.service", (self.root / "client-calls").read_text())
        self.assertTrue((self.root / "client-calls").read_text().endswith(" --print-url\n"))

    def test_explicit_full_install_enables_reporting_without_discarding_existing_configuration(self):
        self.config.write_text(json.dumps({"BackupClient": {"Dashboard": {"Enabled": False},
                                                          "BackupTargets": {"documents": "synthetic"}},
                                           "Database": {"FilePath": "private-state.db"}}))
        result = self.run_installer("--with-dashboard")
        self.assertEqual(result.returncode, 0, result.stderr)
        actual = json.loads(self.config.read_text())
        self.assertTrue(actual["BackupClient"]["Dashboard"]["Enabled"])
        self.assertEqual(actual["BackupClient"]["BackupTargets"], {"documents": "synthetic"})
        self.assertEqual(actual["Database"], {"FilePath": "private-state.db"})

    def test_reinstall_disable_and_remove_preserve_pending_work_history_and_backup_units(self):
        for name in ("state.db", "pending.json", "history.json"):
            (self.install / name).write_bytes(b"preserved " + name.encode())
        before = {path: path.read_bytes() for path in [*self.install.iterdir(), *self.units.iterdir()]}
        for operation in ("--dashboard-only", "--dashboard-only", "--disable-dashboard", "--dashboard-only", "--remove-dashboard"):
            result = self.run_installer(operation)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual({path: path.read_bytes() for path in before}, before)
        self.assertFalse((self.units / "test-backup-dashboard.service").exists())
        self.assertFalse((self.root / "bin/test-backup-open-dashboard").exists())
        self.assertFalse((self.root / "client-calls").exists())
        calls = (self.root / "systemctl-calls").read_text()
        self.assertNotIn("test-backup.service", calls)
        self.assertNotIn("test-backup.timer", calls)

    def test_default_install_does_not_install_or_enable_a_companion(self):
        result = self.run_installer("--synthetic-configure-option")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.root / "client-calls").read_text(), "configure --synthetic-configure-option\n")
        self.assertFalse((self.units / "test-backup-dashboard.service").exists())
        self.assertIn("SuccessExitStatus=2", (self.units / "test-backup.service").read_text())
        self.assertIn("Type=oneshot", (self.units / "test-backup.service").read_text())
        self.assertIn("link " + str(self.units / "test-backup.service"), (self.root / "systemctl-calls").read_text())

    def test_directory_spaces_survive_unit_parsing_and_installed_open_command(self):
        spaced_install = self.root / "installed with spaces"
        self.install.rename(spaced_install)
        self.environment["BACKUP_CLIENT_INSTALL_DIR"] = str(spaced_install)
        self.environment["BACKUP_CLIENT_DASHBOARD_INSTALL_DIR"] = str(self.root / "companion with spaces")
        self.environment["BACKUP_CLIENT_BIN_DIR"] = str(self.root / "bin with spaces")
        result = self.run_installer("--dashboard-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        unit = (self.units / "test-backup-dashboard.service").read_text()
        self.assertIn("WorkingDirectory=" + str(self.root / "installed") + r"\x20with\x20spaces", unit)
        self.assertIn('ExecStart="' + str(self.root / "companion with spaces/FlorisDeV.BackupClient") + '" dashboard', unit)
        result = subprocess.run([str(self.root / "bin with spaces/test-backup-open-dashboard")], env=self.environment)
        self.assertEqual(result.returncode, 0)
        verified = subprocess.run(["systemd-analyze", "verify", str(self.units / "test-backup-dashboard.service")],
                                  capture_output=True, text=True)
        self.assertEqual(verified.returncode, 0, verified.stderr)

    def test_rejects_unsupported_paths_and_unit_names_before_writing_or_launching(self):
        for variable, value in [("BACKUP_CLIENT_UNIT_PREFIX", "backup-client.service;unsafe"),
                                *[("BACKUP_CLIENT_INSTALL_DIR", str(self.root / ("bad" + character)))
                                  for character in ("%", "$", '"', "'", "\\", "\n")]]:
            with self.subTest(variable=variable, value=value):
                environment = dict(self.environment, **{variable: value})
                result = subprocess.run(["bash", str(self.publish / "install.sh"), "--dashboard-only"],
                                        env=environment, capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((self.root / "client-calls").exists())
                self.assertFalse((self.root / "systemctl-calls").exists())

    def test_disabled_reporting_and_public_configuration_fail_without_changing_backup(self):
        for enabled, permissions in ((False, 0o600), (True, 0o644)):
            self.config.write_text(json.dumps({"BackupClient": {"Dashboard": {"Enabled": enabled}}}))
            self.config.chmod(permissions)
            before = self.config.read_bytes()
            result = self.run_installer("--dashboard-only")
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(self.config.read_bytes(), before)
            self.assertFalse((self.root / "systemctl-calls").exists())

    def test_companion_upgrade_and_rollback_leave_backup_payload_and_newer_reporting_untouched(self):
        backup = self.install / "FlorisDeV.BackupClient"
        backup.write_bytes(b"existing backup executable")
        history = self.install / "newer-reporting.json"
        history.write_text('{"schemaVersion":999,"future":"preserve"}')
        before = {path: path.read_bytes() for path in (backup, history, self.config)}
        for release in ("older", "newer", "older"):
            source = self.publish / "FlorisDeV.BackupClient"
            source.write_text('#!/bin/sh\nprintf "%s\\n" ' + release + ' >> "$CLIENT_CALLS"\n')
            source.chmod(0o755)
            self.write_contract()
            result = self.run_installer("--dashboard-only")
            self.assertEqual(result.returncode, 0, result.stderr)
            subprocess.run([str(self.root / "bin/test-backup-open-dashboard")], env=self.environment, check=True)
            self.assertEqual({path: path.read_bytes() for path in before}, before)
        self.assertEqual((self.root / "client-calls").read_text(), "older\nnewer\nolder\n")

    def test_unavailable_user_session_keeps_service_and_recovery_material_for_retry(self):
        installed = self.run_installer("--dashboard-only")
        self.assertEqual(installed.returncode, 0, installed.stderr)
        unit = self.units / "test-backup-dashboard.service"
        before = unit.read_bytes()
        (self.commands / "systemctl").write_text('#!/bin/sh\necho "synthetic unavailable user bus" >&2\nexit 1\n')
        result = self.run_installer("--remove-dashboard")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("user session", result.stderr)
        self.assertEqual(unit.read_bytes(), before)
        self.assertTrue((self.root / "bin/test-backup-open-dashboard").exists())

    def test_retry_with_written_but_not_loaded_companion_unit_succeeds_without_stop(self):
        (self.units / "test-backup-dashboard.service").write_text("previous partial installation")
        (self.commands / "systemctl").write_text('''#!/bin/sh
case "$*" in
  *show*) printf 'LoadState=not-found\\nActiveState=inactive\\n';;
  *stop*) echo 'Cannot stop a unit that is not loaded' >&2; exit 1;;
esac
''')
        result = self.run_installer("--dashboard-only")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_or_mixed_payload_contract_is_rejected_without_executing_the_client(self):
        marker = self.publish / "dashboard-contract.json"
        original = marker.read_bytes()
        marker.unlink()
        result = self.run_installer("--dashboard-only")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("compatible published", result.stderr)
        marker.write_bytes(original)
        (self.publish / "FlorisDeV.BackupClient.dll").write_bytes(b"older incompatible assembly")
        result = self.run_installer("--dashboard-only")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.root / "client-calls").exists())
        self.assertFalse((self.root / "systemctl-calls").exists())

    def test_remove_after_disable_can_remove_external_unit_already_missing_from_manager(self):
        unit = self.units / "test-backup-dashboard.service"
        unit.write_text("disabled external companion unit")
        (self.commands / "systemctl").write_text('''#!/bin/sh
case "$*" in
  *show*) printf 'LoadState=not-found\\nActiveState=inactive\\n';;
  *disable*) echo 'Unit does not exist' >&2; exit 1;;
esac
''')
        result = self.run_installer("--remove-dashboard")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(unit.exists())

    def test_single_file_payload_requires_only_matching_bundle_hash(self):
        (self.publish / "FlorisDeV.BackupClient.dll").unlink()
        self.write_contract(single_file=True)
        result = self.run_installer("--dashboard-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        (self.publish / "FlorisDeV.BackupClient").write_bytes(b"mixed older executable")
        self.assertNotEqual(self.run_installer("--dashboard-only").returncode, 0)

    def test_companion_directory_alias_cannot_replace_the_backup_payload(self):
        executable = self.install / "FlorisDeV.BackupClient"
        executable.write_bytes(b"existing independent backup executable")
        self.environment["BACKUP_CLIENT_DASHBOARD_INSTALL_DIR"] = str(self.root / "elsewhere/../installed")
        result = self.run_installer("--dashboard-only")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(executable.read_bytes(), b"existing independent backup executable")
        self.assertFalse((self.root / "systemctl-calls").exists())


if __name__ == "__main__":
    unittest.main()
