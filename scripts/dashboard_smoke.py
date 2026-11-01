"""Isolated, genuine systemd companion adapter for run-recovery-e2e.py.

Preparation writes private artifacts only. Running the prepared plan requires
review of preflight.json, appsettings.local.json, launcher and unit files.
"""
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import time
from urllib.parse import urlsplit
from urllib.request import Request, urlopen
from urllib.error import HTTPError


class DashboardSmoke:
    def __init__(self, root, tag, environment):
        self.root, self.tag, self.environment = root, tag, environment
        self.prefix = tag + '-backup'
        self.units = root / 'units'
        self.service = self.prefix + '.service'
        self.timer = self.prefix + '.timer'
        self.companion = self.prefix + '-dashboard.service'
        self.created = False
        self.checks = []
        self.base = self.session = None
        self.started_from_browser = False
        self.observer_child = None
        self.installer_environment = {**environment,
            'BACKUP_CLIENT_INSTALL_DIR': str(root / 'client'),
            'BACKUP_CLIENT_DASHBOARD_INSTALL_DIR': str(root / 'companion'),
            'BACKUP_CLIENT_BIN_DIR': str(root / 'bin'),
            'BACKUP_CLIENT_SYSTEMD_USER_DIR': str(self.units),
            'BACKUP_CLIENT_UNIT_PREFIX': self.prefix,
            'BACKUP_CLIENT_SYSTEMD_LAUNCHER': str(root / 'launch-client')}

    def prepare(self, settings):
        dashboard = settings['BackupClient'].setdefault('Dashboard', {})
        dashboard.update(Enabled=True, ReportingDirectory=str(self.root / 'reporting'),
                         LogDirectory=str(self.root / 'data/backup-client/logs'), Port=0,
                         BackupServiceUnit=self.service, BackupTimerUnit=self.timer,
                         MaximumBackupAge='1.00:00:00')
        self.write_config(settings)
        self.units.mkdir(exist_ok=True)
        (self.root / 'environment.json').write_text(json.dumps(self.environment, indent=2))
        launcher = self.root / 'launch-client'
        launcher.write_text('#!' + sys.executable + '\n' +
            'import json, os, sys\n' +
            'environment = json.load(open(' + repr(str(self.root / 'environment.json')) + '))\n' +
            '# Keep the real manager receipt; never forge or replay an invocation.\n' +
            'if "INVOCATION_ID" in os.environ: environment["INVOCATION_ID"] = os.environ["INVOCATION_ID"]\n' +
            'os.execve(sys.argv[1], sys.argv[1:], environment)\n')
        launcher.chmod(0o700)
        (self.units / self.service).write_text(f'''[Unit]
Description=Synthetic Stowhaven smoke backup
[Service]
Type=oneshot
SuccessExitStatus=2
WorkingDirectory={self.root / 'client'}
ExecStart="{launcher}" "{self.root / 'client/FlorisDeV.BackupClient'}" backup --result-file "{self.root / 'managed-result.json'}"
UMask=0077
RuntimeMaxSec=180
StandardOutput=append:{self.root / 'logs/managed-client.log'}
StandardError=inherit
''')
        (self.units / self.timer).write_text(f'''[Unit]
Description=Synthetic Stowhaven smoke timer
[Timer]
OnCalendar=*-*-* 23:59:59
Unit={self.service}
Persistent=true
[Install]
WantedBy=timers.target
''')
        # Pre-generate the exact companion file for review; the installer replaces
        # it with equivalent production content after the execution gate.
        (self.units / self.companion).write_text(f'''[Unit]
Description=Stowhaven local dashboard companion
[Service]
Type=simple
WorkingDirectory={self.root / 'client'}
ExecStart="{launcher}" "{self.root / 'companion/FlorisDeV.BackupClient'}" dashboard --BackupClient:Dashboard:BackupServiceUnit={self.service} --BackupClient:Dashboard:BackupTimerUnit={self.timer}
Restart=on-failure
RestartSec=3
UMask=0077
[Install]
WantedBy=default.target
''')
        # Explicit home path here identifies ONLY possible unique links for review
        # and cleanup. We do not use the isolated XDG_CONFIG_HOME to pretend that
        # an already-running manager has a different persistent unit search path.
        manager_dir = Path.home() / '.config/systemd/user'
        self.managed_links = {str(manager_dir / self.service): str(self.units / self.service),
                              str(manager_dir / self.timer): str(self.units / self.timer),
                              str(manager_dir / self.companion): str(self.units / self.companion),
                              str(manager_dir / 'timers.target.wants' / self.timer): str(self.units / self.timer),
                              str(manager_dir / 'default.target.wants' / self.companion): str(self.units / self.companion)}
        manifest = {'root': str(self.root), 'prefix': self.prefix,
                    'units': [self.service, self.timer, self.companion],
                    'persistentLinks': self.managed_links,
                    'launchEnvironment': str(self.root / 'environment.json'),
                    'launcher': str(launcher), 'clientConfig': str(self.root / 'client/appsettings.local.json'),
                    'sourceTargets': settings['BackupClient']['BackupTargets'],
                    'database': settings['Database']['FilePath'],
                    'apiUrl': settings['BackupApiClient']['ApiUrl'],
                    'cleanup': 'Stop only listed unique units; remove only listed symlinks still targeting owned unit files; daemon-reload; remove exact test containers.'}
        (self.root / 'preflight.json').write_text(json.dumps(manifest, indent=2))
        self.validate(settings)

    def validate(self, settings):
        root = self.root.resolve()
        assert root.stat().st_mode & 0o777 == 0o700
        for path in [*settings['BackupClient']['BackupTargets'].values(), settings['Database']['FilePath'],
                     settings['BackupClient']['Dashboard']['ReportingDirectory'],
                     settings['BackupClient']['Dashboard']['LogDirectory'],
                     settings['BackupClient']['Encryption']['RecoveryPhraseFilePath']]:
            assert Path(path).is_absolute() and Path(path).resolve().is_relative_to(root), path
        assert urlsplit(settings['BackupApiClient']['ApiUrl']).hostname == '127.0.0.1'
        assert settings['BackupClient']['Schedule']['Enabled'] is False
        assert settings['BackupClient']['Dashboard']['BackupServiceUnit'] == self.service
        assert settings['BackupClient']['Dashboard']['BackupTimerUnit'] == self.timer
        assert set(settings['BackupClient']['BackupTargets']) == {'fixture'}
        for service, assembly in [('client', 'FlorisDeV.BackupClient'), ('api', 'FlorisDeV.BackupApi'), ('worker', 'FlorisDeV.BackupWorker')]:
            # Attribute metadata strings occur in the assembly's #Blob heap.
            # The unique value plus UserSecretsIdAttribute proves fresh metadata;
            # the preflight also retains the unique publish command/log.
            artifact = root / service / (assembly + '.dll')
            if service == 'client' and not artifact.exists():
                artifact = root / service / assembly
            content = artifact.read_bytes()
            assert self.tag.encode() in content and b'UserSecretsIdAttribute' in content, service
        for candidate in root.glob('*/appsettings*.json'):
            assert not candidate.is_symlink()
            data = json.loads(candidate.read_text())
            targets = data.get('BackupClient', {}).get('BackupTargets', {})
            assert not targets or targets == settings['BackupClient']['BackupTargets'], candidate
        for link in self.managed_links:
            assert not os.path.lexists(link), 'Refusing preexisting unit/link: ' + link

    def write_config(self, settings):
        path = self.root / 'client/appsettings.local.json'
        temporary = path.with_name('.smoke-appsettings.local.json.tmp')
        temporary.write_text(json.dumps(settings, indent=2))
        temporary.chmod(0o600)
        os.replace(temporary, path)

    def ctl(self, *arguments, check=True, env=None):
        # systemctl needs the actual user manager search path; the payload itself
        # gets only the isolated environment via launch-client.
        manager_environment = {key: value for key, value in self.environment.items()
                               if key in ('PATH', 'HOME', 'USER', 'LOGNAME', 'XDG_RUNTIME_DIR', 'DBUS_SESSION_BUS_ADDRESS')}
        process = subprocess.run(['systemctl', '--user', *arguments], env=env or manager_environment,
                                 capture_output=True, text=True, timeout=25)
        if check and process.returncode:
            raise RuntimeError('systemctl ' + arguments[0] + ' failed: ' + process.stderr[:500])
        return process

    def state(self, unit):
        output = self.ctl('show', '--all', '--timestamp=unix',
                          '--property=LoadState,ActiveState,SubState,UnitFileState,Type,InvocationID,Result,ExecMainStatus,MainPID,NextElapseUSecRealtime,LastTriggerUSec,Unit,FragmentPath', '--', unit).stdout
        return dict(line.split('=', 1) for line in output.splitlines() if '=' in line)

    def install(self, proxy):
        manifest = json.loads((self.root / 'preflight.json').read_text())
        self.managed_links = manifest['persistentLinks']
        for path in self.managed_links:
            assert not os.path.lexists(path), path
        self.created = True  # cleanup is registered before first mutation
        self.ctl('link', str(self.units / self.service), str(self.units / self.timer), str(self.units / self.companion))
        self.ctl('daemon-reload')
        self.ctl('enable', '--now', str(self.units / self.timer))
        self.installer('--dashboard-only')
        timer = self.state(self.timer)
        assert timer['UnitFileState'] == 'enabled' and timer['ActiveState'] == 'active', timer
        assert self.state(self.companion)['UnitFileState'] == 'enabled'
        self.checks.append({'persistentTimer': timer, 'companion': self.state(self.companion)})
        self.authorize()
        status = self.http('/api/status')
        assert status['scheduling']['state'] == 'enabled', status
        self.checks.append('idle installed dashboard access; actual persistent timer enabled')
        self.reload_configuration()
        proxy.hold_next_start = True
        self.proxy = proxy
        (self.root / 'ready-for-browser').write_text('Companion running. Use isolated installed open command with environment.json.\n')
        deadline = time.monotonic() + 240
        while not (self.root / 'browser-check-complete').exists():
            if time.monotonic() >= deadline:
                raise RuntimeError('Timed out waiting for the actual installed-browser smoke')
            time.sleep(0.2)
        if not proxy.held.wait(timeout=15):
            raise RuntimeError('Browser must click Start backup before signaling completion')
        self.started_from_browser = True
        self.checks.append('actual installed browser authorized, requested Start backup and observed active execution')
        self.active_contention(proxy)

    def reload_configuration(self):
        settings = json.loads((self.root / 'client/appsettings.local.json').read_text())
        original = settings['BackupClient']['Schedule']['Enabled']
        try:
            settings['BackupClient']['Schedule']['Enabled'] = True
            self.write_config(settings)
            self.expect_schedule('incompatibleSchedule')
        finally:
            settings['BackupClient']['Schedule']['Enabled'] = original
            self.write_config(settings)
        self.expect_schedule('enabled')
        assert not (self.root / 'client.db').exists()
        self.checks.append('installed companion follows atomic shared-config edit and restore without restart or backup-state creation')

    def expect_schedule(self, expected):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            actual = self.http('/api/status')['scheduling']['state']
            if actual == expected:
                return
            time.sleep(0.1)
        raise AssertionError('Expected scheduling ' + expected + ', got ' + actual)

    def installer(self, operation):
        environment = {**self.installer_environment}
        # The installer talks to the real manager; its spawned service still
        # runs through the scrubbed launcher recorded in preflight.json.
        environment.pop('XDG_CONFIG_HOME', None)
        process = subprocess.run(['bash', str(self.root / 'client/install.sh'), operation], env=environment,
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=45)
        if process.returncode:
            raise RuntimeError('installer ' + operation + ' failed: ' + process.stderr[:500])

    def authorize(self):
        opener = self.root / 'bin' / (self.prefix + '-open-dashboard')
        deadline = time.monotonic() + 20
        while True:
            process = subprocess.run([str(opener), '--print-url'], env=self.environment,
                                     capture_output=True, text=True, timeout=10)
            if process.returncode == 0:
                break
            if time.monotonic() >= deadline:
                raise RuntimeError('Installed open command could not authorize')
            time.sleep(0.1)
        opened = urlsplit(process.stdout.strip())
        self.base = opened.scheme + '://' + opened.netloc
        self.session = self.http('/api/session', {'ticket': opened.fragment}, authorized=False)['session']
        try:
            self.http('/api/status', authorized=False)
            raise AssertionError('Unauthorized status succeeded')
        except HTTPError as error:
            assert error.code == 401

    def http(self, endpoint, body=None, authorized=True):
        headers = {'Content-Type': 'application/json', 'Origin': self.base}
        if authorized:
            headers['Authorization'] = 'Bearer ' + self.session
        data = None if body is None else json.dumps(body).encode()
        with urlopen(Request(self.base + endpoint, data=data, headers=headers), timeout=20) as response:
            content = response.read()
            return json.loads(content) if content else None

    def client(self, name, accepted):
        path = self.root / 'managed-result.json'
        if not (name == 'submitted' and self.started_from_browser):
            path.unlink(missing_ok=True)
            self.ctl('reset-failed', self.service, check=False)
            self.ctl('start', '--no-block', self.service)
        deadline = time.monotonic() + 150
        while time.monotonic() < deadline:
            state = self.state(self.service)
            if state['ActiveState'] not in ('active', 'activating', 'deactivating') and path.exists():
                break
            time.sleep(0.1)
        else:
            raise RuntimeError('Managed backup did not finish')
        result = json.loads(path.read_text())
        (self.root / (name + '-result.json')).write_text(json.dumps(result))
        assert int(state['ExecMainStatus']) in accepted, state
        receipt = json.loads((self.root / 'reporting/execution.json').read_text())
        assert receipt['invocationId'].lower() == state['InvocationID'].lower(), (receipt, state)
        assert receipt['completedAt'] is not None
        self.checks.append({'backup': name, 'outcome': result['outcome'], 'attemptId': receipt['attemptId'],
                            'receiptMatchesManager': True, 'execStatus': state['ExecMainStatus']})
        return result

    def active_contention(self, proxy):
        active = self.state(self.service)
        receipt = json.loads((self.root / 'reporting/execution.json').read_text())
        assert active['MainPID'] != '0' and receipt['invocationId'].lower() == active['InvocationID'].lower()
        assert self.http('/api/start', {})['state'] == 'alreadyRunning'
        duplicate = subprocess.run([str(self.root / 'client/FlorisDeV.BackupClient'), 'backup',
                                    '--result-file', str(self.root / 'duplicate-result.json')],
                                   env=self.environment, cwd=self.root / 'client', capture_output=True, timeout=20)
        assert duplicate.returncode == 1
        assert json.loads((self.root / 'duplicate-result.json').read_text())['errorType'] == 'BackupAlreadyRunningException'
        timer_path = self.units / self.timer
        original_timer = timer_path.read_bytes()
        previous_trigger = self.state(self.timer).get('LastTriggerUSec')
        try:
            timer_path.write_bytes(original_timer + b'\n[Timer]\nOnActiveSec=1s\n')
            self.ctl('daemon-reload')
            self.ctl('restart', self.timer)
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                triggered = self.state(self.timer).get('LastTriggerUSec')
                if triggered and triggered not in ('0', 'n/a') and triggered != previous_trigger:
                    break
                time.sleep(0.1)
            else:
                raise AssertionError('Actual test timer did not trigger while backup was active')
        finally:
            timer_path.write_bytes(original_timer)
            self.ctl('daemon-reload')
            self.ctl('restart', self.timer)
        protected = [self.units / self.service, timer_path, self.root / 'client/appsettings.local.json']
        original = {path: path.read_bytes() for path in protected}
        for operation in ('--disable-dashboard', '--remove-dashboard', '--dashboard-only'):
            self.installer(operation)
            current = self.state(self.service)
            assert current['MainPID'] == active['MainPID'] and current['InvocationID'] == active['InvocationID']
            assert self.state(self.timer)['ActiveState'] == 'active'
        assert {path: path.read_bytes() for path in protected} == original
        self.authorize()
        status = self.http('/api/status')
        assert status['executionState'] == 'active' and status['snapshot']['attemptId'] == receipt['attemptId']
        self.checks.append({'activeStart': 'actual browser /api/start', 'duplicateManual': 'alreadyRunning',
                            'duplicateCLI': 'BackupAlreadyRunningException', 'actualTimerTrigger': triggered,
                            'sameActiveInvocationAcrossCompanionRemoval': True})
        proxy.release.set()

    def interrupted_recovery(self, proxy, settings):
        (self.root / 'source/file-000.txt').write_text('Synthetic interrupted-attempt change\n')
        proxy.held.clear()
        proxy.release.clear()
        proxy.abort_held = False
        proxy.hold_next_start = True
        result = self.http('/api/start', {})
        assert result['state'] == 'started', result
        assert proxy.held.wait(timeout=15)
        active_attempt = self.http('/api/status')['snapshot']['attemptId']
        self.ctl('kill', '--signal=SIGKILL', '--kill-whom=main', self.service)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            observed = self.http('/api/status')
            if observed['observation'] == 'interrupted':
                break
            time.sleep(0.1)
        else:
            raise AssertionError('Killed real client did not show interruption')
        proxy.abort_held = True  # never forward the killed client's unsubmitted run
        proxy.release.set()
        self.checks.append({'realClientKill': active_attempt, 'observation': observed['observation']})
        # The proxy request returns before it can be armed again; subsequent work
        # runs without a hold and reconciles using the genuine API/worker.
        self.write_config(settings)
        recovered = self.client('client-restarted', (0,))
        assert recovered['outcome'] == 'succeeded', recovered

    def compatibility_and_unavailable_session(self, settings):
        originals = {}
        for name in ('snapshot.json', 'history.json', 'clean-evidence.json'):
            path = self.root / 'reporting' / name
            originals[path] = path.read_bytes() if path.exists() else None
            path.write_text('{"schemaVersion":999,"future":"preserve-synthetic"}')
            path.chmod(0o600)
        expected = {path: path.read_bytes() for path in originals}
        self.ctl('restart', self.companion)
        self.authorize()
        assert self.http('/api/status')['state'] == 'incompatible'
        assert self.http('/api/history')['state'] == 'incompatible'
        self.client('future-reporting-preserved', (0,))
        assert {path: path.read_bytes() for path in originals} == expected
        for path, content in originals.items():
            if content is None:
                path.unlink()
            else:
                path.write_bytes(content)
        self.checks.append('older reader and real producer preserve unsupported newer snapshot/history/clean evidence across restart')
        settings['BackupClient']['Schedule']['Enabled'] = True
        self.write_config(settings)
        self.ctl('restart', self.companion)
        self.authorize()
        assert self.http('/api/status')['scheduling']['state'] == 'incompatibleSchedule'
        try:
            self.http('/api/start', {})
            raise AssertionError('Resident scheduling start was accepted')
        except HTTPError as error:
            assert error.code == 503 and json.loads(error.read())['state'] == 'incompatibleSchedule'
        settings['BackupClient']['Schedule']['Enabled'] = False
        self.write_config(settings)
        self.ctl('stop', self.companion)
        unavailable_environment = {**self.environment,
            'DBUS_SESSION_BUS_ADDRESS': 'unix:path=' + str(self.root / 'missing-user-bus'),
            'XDG_RUNTIME_DIR': str(self.root / 'missing-runtime')}
        with (self.root / 'logs/session-unavailable-observer.log').open('w') as output:
            self.observer_child = subprocess.Popen([str(self.root / 'client/FlorisDeV.BackupClient'), 'dashboard'],
                env=unavailable_environment, cwd=self.root / 'client', stdout=output, stderr=subprocess.STDOUT)
            try:
                self.authorize()
                assert self.http('/api/status')['scheduling']['state'] == 'userSessionUnavailable'
                try:
                    self.http('/api/start', {})
                    raise AssertionError('Unavailable user session start was accepted')
                except HTTPError as error:
                    assert error.code == 503 and json.loads(error.read())['state'] == 'userSessionUnavailable'
            finally:
                self.observer_child.terminate()
                try:
                    self.observer_child.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    self.observer_child.kill()
                    self.observer_child.wait(timeout=10)
        self.ctl('start', self.companion)
        self.authorize()
        self.checks.append('resident schedule refused; actual unavailable bus diagnosed without stopping real user manager')

    def lifecycle_pending(self):
        paths = [self.units / self.service, self.units / self.timer, self.root / 'client/appsettings.local.json',
                 self.root / 'client.db']
        # Instance discovery/authorization rotates on companion restart; only
        # portable durable observations and the execution receipt are retained.
        paths.extend(path for name in ('snapshot.json', 'history.json', 'clean-evidence.json', 'execution.json')
                     if (path := self.root / 'reporting' / name).exists())
        before = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}
        self.installer('--disable-dashboard')
        assert self.state(self.timer)['ActiveState'] == 'active'
        self.installer('--remove-dashboard')
        assert self.state(self.timer)['UnitFileState'] == 'enabled'
        self.installer('--dashboard-only')
        assert before == {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}
        self.authorize()
        status = self.http('/api/status')
        assert status['snapshot']['result'] == 'pending', status
        self.checks.append('disable/remove/reinstall companion preserves actual pending SQLite, reporting, config and backup units byte-for-byte')

    def complete(self, settings):
        self.authorize()
        status = self.http('/api/status')
        history = self.http('/api/history')
        logs = self.http('/api/logs')
        self.checks.append({'authorizedStatus': status, 'history': history, 'logsAvailable': logs.get('state')})
        with sqlite3.connect(self.root / 'client.db') as database:
            tables = [row[0] for row in database.execute("SELECT name FROM sqlite_master WHERE type='table'")]
            pending = [table for table in tables if table.lower().replace('_', '') == 'pendingbackupruns']
            assert pending and database.execute('SELECT COUNT(*) FROM "' + pending[0] + '"').fetchone()[0] == 0
        old_session = self.session
        self.ctl('restart', self.companion)
        self.authorize()
        current = self.session
        self.session = old_session
        try:
            self.http('/api/status')
            raise AssertionError('Restart preserved old authorization')
        except HTTPError as error:
            assert error.code == 401
        finally:
            self.session = current
        self.checks.append('companion restart requires reauthorization; history survives; local pending cleared')
        self.compatibility_and_unavailable_session(settings)
        (self.root / 'dashboard-summary.json').write_text(json.dumps({'checks': self.checks}, indent=2))

    def cleanup(self):
        if not self.created:
            return
        errors = []
        if hasattr(self, 'proxy'):
            self.proxy.abort_held = True
            self.proxy.release.set()
        if self.observer_child is not None and self.observer_child.poll() is None:
            self.observer_child.kill()
            self.observer_child.wait(timeout=10)
        for unit in (self.timer, self.companion, self.service):
            try:
                if self.state(unit).get('LoadState') == 'not-found':
                    continue
                self.ctl('stop', unit)
            except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
                errors.append('Stop ' + unit + ': ' + type(error).__name__)
                # Only manifest-owned units can reach this path. A slow stop
                # must not prevent cleanup of the other units/resources.
                for arguments in [('kill', '--signal=SIGKILL', '--kill-whom=all', unit),
                                  ('stop', '--no-block', unit)]:
                    try:
                        self.ctl(*arguments, check=False)
                    except (OSError, RuntimeError, subprocess.TimeoutExpired) as fallback:
                        errors.append('Fallback ' + unit + ': ' + type(fallback).__name__)
        for link, target in self.managed_links.items():
            path = Path(link)
            try:
                if path.is_symlink() and path.resolve() == Path(target).resolve():
                    path.unlink()
                elif os.path.lexists(path):
                    errors.append('Unexpected link retained: ' + link)
            except OSError as error:
                errors.append('Unlink ' + link + ': ' + type(error).__name__)
        for arguments in [('daemon-reload',), ('reset-failed', self.service, self.timer, self.companion)]:
            try:
                self.ctl(*arguments, check=False)
            except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
                errors.append(arguments[0] + ': ' + type(error).__name__)
        remaining = [link for link in self.managed_links if os.path.lexists(link)]
        states = {}
        for unit in (self.service, self.timer, self.companion):
            try:
                states[unit] = self.state(unit)
            except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
                states[unit] = {'error': type(error).__name__}
                errors.append('Show ' + unit + ': ' + type(error).__name__)
        evidence = {'remainingLinks': remaining, 'errors': errors, 'units': states}
        (self.root / 'dashboard-cleanup.json').write_text(json.dumps(evidence, indent=2))
        assert not remaining and not errors, evidence
