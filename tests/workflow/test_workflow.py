import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


REPOSITORY = Path(__file__).resolve().parents[2]
ZERO = "0" * 40
COMMIT = "1" * 40


class HookTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="stowhaven-hooks-")
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        self.root = self.directory / "repository with spaces"
        self.root.mkdir()
        shutil.copytree(REPOSITORY / ".githooks", self.root / ".githooks")
        (self.root / "scripts").mkdir()
        shutil.copy2(REPOSITORY / "scripts/install-git-hooks.sh", self.root / "scripts/install-git-hooks.sh")
        self.environment = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
        self.environment.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull, GIT_TERMINAL_PROMPT="0")
        self.git("init", "--quiet", "--initial-branch=main")
        self.git("config", "user.name", "Workflow Test")
        self.git("config", "user.email", "workflow@example.invalid")
        self.git("commit", "--quiet", "--allow-empty", "-m", "baseline")

    def run_command(self, arguments, **options):
        return subprocess.run(arguments, cwd=self.root, env=self.environment, capture_output=True,
                              text=True, **options)

    def git(self, *arguments):
        result = self.run_command(["git", *arguments])
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout.strip()

    def install(self):
        return self.run_command(["sh", "scripts/install-git-hooks.sh"])

    def test_installed_hook_blocks_main_and_allows_feature_commits(self):
        self.assertEqual(self.install().returncode, 0)
        result = self.run_command(["git", "commit", "--allow-empty", "-m", "blocked"])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("protected main", result.stderr)
        self.git("switch", "--quiet", "-c", "feature/test")
        self.git("commit", "--quiet", "--allow-empty", "-m", "allowed")

    def test_guards_survive_missing_worktree_sources_and_block_detached_commits(self):
        self.assertEqual(self.install().returncode, 0)
        shutil.rmtree(self.root / ".githooks")
        result = self.run_command(["git", "commit", "--allow-empty", "-m", "blocked"])
        self.assertNotEqual(result.returncode, 0)
        self.git("switch", "--quiet", "--detach")
        result = self.run_command(["git", "commit", "--allow-empty", "-m", "blocked detached"])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("feature branch", result.stderr)

    def test_installed_guards_work_in_linked_worktrees(self):
        self.assertEqual(self.install().returncode, 0)
        worktree = self.directory / "linked worktree"
        self.git("worktree", "add", "--quiet", "-b", "feature/linked", str(worktree))
        result = subprocess.run(["git", "commit", "--quiet", "--allow-empty", "-m", "linked feature"],
                                cwd=worktree, env=self.environment, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_pre_push_checks_every_destination_including_deletion(self):
        self.assertEqual(self.install().returncode, 0)
        hook = Path(self.git("config", "--get", "core.hooksPath")) / "pre-push"
        allowed = f"refs/heads/main {COMMIT} refs/heads/feature/test {ZERO}\n"
        allowed += f"refs/tags/main {COMMIT} refs/tags/main {ZERO}\n"
        self.assertEqual(self.run_command([str(hook), "origin", "unused"], input=allowed).returncode, 0)
        for update in (f"refs/heads/feature/test {COMMIT} refs/heads/main {ZERO}\n",
                       f"(delete) {ZERO} refs/heads/main {COMMIT}\n"):
            with self.subTest(update=update):
                result = self.run_command([str(hook), "origin", "unused"], input=allowed + update)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("protected main", result.stderr)

    def test_real_push_blocks_feature_to_main_but_allows_feature_destination(self):
        self.assertEqual(self.install().returncode, 0)
        self.git("switch", "--quiet", "-c", "feature/test")
        self.git("commit", "--quiet", "--allow-empty", "-m", "feature")
        remote = self.directory / "remote.git"
        self.git("init", "--quiet", "--bare", str(remote))
        self.git("remote", "add", "origin", str(remote))
        result = self.run_command(["git", "push", "origin", "HEAD:refs/heads/main"])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("protected main", result.stderr)
        self.git("push", "origin", "HEAD:refs/heads/feature/test")

    def test_installer_preserves_custom_hooks_configuration(self):
        self.git("config", "core.hooksPath", "custom-hooks")
        result = self.install()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.git("config", "--get", "core.hooksPath"), "custom-hooks")

    def test_installer_preserves_active_default_hooks(self):
        hook = self.root / ".git/hooks/post-checkout"
        content = "#!/bin/sh\nexit 0\n"
        hook.write_text(content)
        hook.chmod(0o755)
        self.assertNotEqual(self.install().returncode, 0)
        self.assertEqual(hook.read_text(), content)
        self.assertNotEqual(self.run_command(["git", "config", "--get", "core.hooksPath"]).returncode, 0)

    def test_installer_can_be_run_again_without_changing_repository_policy(self):
        self.assertEqual(self.install().returncode, 0)
        installed = self.git("config", "--get", "core.hooksPath")
        self.assertEqual(self.install().returncode, 0)
        self.assertEqual(self.git("config", "--get", "core.hooksPath"), installed)
        self.assertNotEqual(self.run_command(["git", "commit", "--allow-empty", "-m", "still blocked"]).returncode, 0)


class VerificationTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="stowhaven-verify-test-")
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        self.root = self.directory / "repository with spaces"
        (self.root / "scripts").mkdir(parents=True)
        shutil.copy2(REPOSITORY / "scripts/verify.sh", self.root / "scripts/verify.sh")
        self.commands = self.directory / "commands.jsonl"
        binaries = self.directory / "bin"
        binaries.mkdir()
        # The absolute interpreter avoids recursively invoking the fake python3.
        tool = f"#!{sys.executable}\n" + '''import json, os, sys
from pathlib import Path
name = Path(sys.argv[0]).name
with open(os.environ["WORKFLOW_COMMANDS"], "a") as output:
    output.write(json.dumps([name, *sys.argv[1:]]) + "\\n")
if len(sys.argv) > 1 and os.environ.get("WORKFLOW_FAIL") == name + ":" + sys.argv[1]:
    print("controlled failure", file=sys.stderr)
    sys.exit(23)
print("controlled tool success")
'''
        for name in ("dotnet", "python3", "az"):
            executable = binaries / name
            executable.write_text(tool)
            executable.chmod(0o755)
        self.environment = dict(os.environ, PATH=str(binaries) + os.pathsep + os.environ["PATH"],
                                WORKFLOW_COMMANDS=str(self.commands), TMPDIR=str(self.directory))

    def run_verification(self, *arguments):
        return subprocess.run(["bash", str(self.root / "scripts/verify.sh"), *arguments],
                              cwd=self.directory, env=self.environment, capture_output=True, text=True)

    def recorded_commands(self):
        return [json.loads(line) for line in self.commands.read_text().splitlines()] if self.commands.exists() else []

    def test_restores_before_build_and_runs_the_existing_ci_checks(self):
        result = self.run_verification()
        self.assertEqual(result.returncode, 0, result.stderr)
        commands = self.recorded_commands()
        self.assertEqual([(c[0], c[1]) for c in commands],
                         [("dotnet", "restore"), ("dotnet", "build"), ("dotnet", "publish"),
                          ("dotnet", "test"), ("python3", "-m"), ("python3", "-m")])
        self.assertIn("--no-restore", commands[1])
        self.assertIn("--no-restore", commands[2])
        self.assertIn("--no-build", commands[3])
        self.assertIn("tests/deployment", commands[4])
        self.assertIn("tests/workflow", commands[5])
        output = Path(commands[2][commands[2].index("--output") + 1])
        self.assertTrue(output.is_relative_to(self.directory))
        self.assertFalse(output.is_relative_to(self.root))
        self.assertIn("All verification checks passed", result.stdout)

    def test_optional_bicep_compilation_uses_stdout_without_generated_repo_files(self):
        result = self.run_verification("--bicep")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.recorded_commands()[-1],
                         ["az", "bicep", "build", "--file", "deploy/bicep/main.bicep", "--stdout"])

    def test_failing_build_stops_later_checks_and_reports_the_saved_log(self):
        self.environment["WORKFLOW_FAIL"] = "dotnet:build"
        result = self.run_verification()
        self.assertEqual(result.returncode, 23)
        self.assertEqual([c[1] for c in self.recorded_commands()], ["restore", "build"])
        self.assertIn("controlled failure", result.stderr)
        self.assertIn("full log:", result.stderr)
        self.assertNotIn("All verification checks passed", result.stdout)

    def test_help_and_unknown_options_do_not_run_checks(self):
        self.assertEqual(self.run_verification("--help").returncode, 0)
        self.assertEqual(self.run_verification("--unknown").returncode, 2)
        self.assertEqual(self.recorded_commands(), [])


if __name__ == "__main__":
    unittest.main()
