"""Deployment tests: every write goes to a temporary installation and unit dir."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from types import SimpleNamespace

MODULE = Path(__file__).resolve().parents[1] / 'perga_ctl.py'
sys.path.insert(0, str(MODULE.parent))
spec = importlib.util.spec_from_file_location('perga_ctl', MODULE)
ctl = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ctl)
import perga_manifest as manifest_module
import perga_systemd as systemd_module
from unittest.mock import patch


class Harness(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name)
        self.root = self.base / 'instance'
        self.events = []
        self.busy = 0
        self.fail_release = None
        self.fail_reload = False
        self.migrate_on_start = False
        self.controller = ctl.Controller(self.root, name='demo', http=self.http,
                                         systemctl=self.systemctl, timeout=0, sleep=lambda _: None)
        self.old = self.bundle('r1')
        self.controller.install(self.args(self.archive(self.old)))
        self.events.clear()

    def bundle(self, name, host='host-v1', web='web-v1', templates='templates-v1', term='term-v1', schema=1):
        path = self.base / name
        path.mkdir()
        for key, content, filename in [('host', host, 'Perga.Host'), ('web', web, 'index.html'),
                                       ('templates', templates, 'dev.perga.js'),
                                       ('termBridge', term, 'perga-term-bridge')]:
            (path / key).mkdir()
            (path / key / filename).write_text(content)
            if key in ('host', 'termBridge'):
                (path / key / filename).chmod(0o755)
        ctl.make_manifest(path, name, schema)
        return path

    def archive(self, source):
        target = self.base / (source.name + '.tar.gz')
        with tarfile.open(target, 'w:gz') as tar:
            for path in source.iterdir():
                tar.add(path, arcname=path.name)
        return target

    def args(self, tarball=None, **kwargs):
        values = dict(from_tarball=str(tarball) if tarball else None, from_repo=None, from_release=None,
                      unit_dir=str(self.base / 'units'), host_url='http://127.0.0.1:4240', no_start=True,
                      force=False, restart_term_bridge=False, notify_track_id='track-1')
        values.update(kwargs)
        return SimpleNamespace(**values)

    def systemctl(self, args):
        self.events.append(('systemctl', *args))
        if args[0] == 'start' and args[1] == 'demo.service' and self.migrate_on_start:
            release = self.controller.current()[1]['releaseId']
            if release == self.fail_release:
                with sqlite3.connect(self.controller.db) as db:
                    db.execute('INSERT INTO schema_migrations VALUES (2)')
                    db.execute("UPDATE content SET value='candidate write'")
        return subprocess.CompletedProcess(args, 3 if args[0] == 'is-active' else 0,
                                            stdout='inactive\n' if args[0] == 'is-active' else '', stderr='')

    def http(self, method, path, body=None):
        self.events.append(('http', method, path, body))
        release = self.controller.current()[1]
        if path.endswith('/drain'):
            self.draining = body.get('draining', True)
            return {'accepted': True}
        if path.endswith('/reload'):
            if self.fail_reload and release['releaseId'] == self.fail_release:
                raise ctl.DeployError('reload rejected')
            return {}
        if path == '/healthz':
            return {'status': 'bad' if release['releaseId'] == self.fail_release else 'ok'}
        if path.endswith('/info'):
            return dict(releaseId=release['releaseId'], webBuildId=release['webBuildId'],
                        schemaVersion=release['schemaVersion'], draining=getattr(self, 'draining', False) if not any(
                            event[:2] == ('systemctl', 'start') for event in self.events) else False,
                        activeDispatches=self.busy)
        raise AssertionError(path)

    def create_db(self):
        with sqlite3.connect(self.controller.db) as db:
            db.execute('CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY)')
            db.execute('INSERT INTO schema_migrations VALUES (1)')
            db.execute('CREATE TABLE content(value TEXT)')
            db.execute("INSERT INTO content VALUES ('old data')")

    def host_entrypoint(self):
        return [sys.executable, str(self.root / 'bin/perga_host.py'), '--name', 'demo',
                '--term-socket', '/run/user/1000/demo/term.sock']

    def test_install_layout_units_and_config(self):
        self.assertEqual(os.readlink(self.root / 'current'), 'releases/r1')
        self.assertEqual(ctl.verify_release(self.root / 'current')['releaseId'], 'r1')
        host = (self.base / 'units/demo.service').read_text()
        term = (self.base / 'units/demo-term.service').read_text()
        self.assertIn('--term-socket %t/demo/term.sock', host)
        self.assertNotIn('Environment', host)
        self.assertIn(f'ExecStart=/usr/bin/python3 {self.root}/bin/perga_host.py', host)
        self.assertNotIn('PartOf=', term)
        self.assertIn('RuntimeDirectoryMode=0700', term)
        self.assertEqual((self.root / 'config/perga.toml').stat().st_mode & 0o777, 0o600)
        self.assertTrue(os.access(self.root / 'bin/perga-ctl', os.X_OK))
        self.assertEqual(self.controller.status()['current'], 'releases/r1')
        with self.assertRaisesRegex(ctl.DeployError, 'existing installation'):
            self.controller.install(self.args(self.archive(self.old)))

    def test_default_toml_launches_directly_without_ambient_proxy(self):
        host = self.root / 'current/host/Perga.Host'
        host.write_text('#!/bin/sh\nprintf "%s\\n" "$HTTP_PROXY" "$https_proxy" "$NO_PROXY" "$@"\n')
        env = dict(os.environ, PERGA_ENABLE_OVERLAYFS='1', HTTP_PROXY='http://example.test:2080',
                   https_proxy='http://example.test:2080', NO_PROXY='example.test')
        result = subprocess.run(self.host_entrypoint(), env=env, capture_output=True, text=True, check=True)
        lines = result.stdout.splitlines()
        self.assertEqual(lines[:3], ['', '', ''])
        self.assertIn('--Perga:AutoManageTerminalBridge=false', lines)
        self.assertIn('--Perga:TerminalBridgeEndpoint=http+unix:///run/user/1000/demo/term.sock', lines)
        self.assertIn('--Perga:CodexWorkerHomeDirectory=', lines)

    def test_toml_drives_launch_and_provider_paths(self):
        config = self.root / 'config/perga.toml'
        config.write_text(
            '[instance]\nname = "demo"\nhost_url = "http://127.0.0.1:4240"\n'
            'overlayfs = true\nworkspaces_dir = "/custom workspaces"\n'
            '\n[providers.codex]\nexecutable = "/tools/bin/codex"\nworker_home = "/private/codex"\n'
            '\n[providers.agy]\nexecutable = "/tools/bin/agy"\n'
            '\n[providers.opencode]\nexecutable = "/tools/bin/opencode"\n'
            '\n[providers.claude]\nexecutable = "/tools/bin/claude"\n'
            '\n[proxy]\nurl = "http://127.0.0.1:2080"\nno_proxy = ["internal.test"]\n')
        launcher = self.root / 'bin/perga-overlayfs-launch.py'
        source = MODULE.parent.parent / 'perga-overlayfs/launch.py'
        self.assertEqual(launcher.read_bytes(), source.read_bytes())
        launcher.write_text(
            "import json, os, sys\n"
            "print(json.dumps([sys.argv[1:], os.environ['HTTPS_PROXY'], os.environ['no_proxy'], "
            "os.environ['ASPNETCORE_URLS']]))\n")
        result = subprocess.run(self.host_entrypoint(), capture_output=True, text=True, check=True)
        arguments, proxy, exclusions, aspnet_urls = json.loads(result.stdout)
        self.assertEqual(arguments[0:5], ['--data-dir', str((self.root / 'data').resolve()),
                                          '--workspaces', '/custom workspaces', '--'])
        self.assertIn('--urls=http://127.0.0.1:4240', arguments)
        self.assertIn('--Perga:CodexWorkerHomeDirectory=/private/codex', arguments)
        self.assertIn('--Perga:ProviderExecutables:agy=/tools/bin/agy', arguments)
        self.assertIn('--Perga:ProviderExecutables:opencode=/tools/bin/opencode', arguments)
        self.assertIn('--Perga:ProviderExecutables:codex=/tools/bin/codex', arguments)
        self.assertIn('--Perga:ProviderExecutables:claude=/tools/bin/claude', arguments)
        self.assertIn('--Perga:AutoManageTerminalBridge=true', arguments)
        self.assertEqual(proxy, 'http://127.0.0.1:2080')
        self.assertEqual(exclusions, 'localhost,127.0.0.1,::1,internal.test')
        self.assertEqual(aspnet_urls, self.controller.validate_config().host_url)

    def test_reconfigure_services_refreshes_units_without_restarting(self):
        config = self.root / 'config/perga.toml'
        content = config.read_text()
        unit = self.base / 'units/demo.service'
        unit.write_text('[Service]\nExecStart=/old/host\n')
        self.controller.configure_services(self.args())
        self.assertIn('--term-socket %t/demo/term.sock', unit.read_text())
        self.assertFalse(any(event[1] in ('start', 'stop', 'restart') for event in self.events))
        self.assertEqual(config.read_text(), content)

    def test_invalid_toml_is_rejected(self):
        config = self.root / 'config/perga.toml'
        config.write_text(config.read_text() + 'overlayfs = "false"\n')
        with self.assertRaisesRegex(ctl.DeployError, 'instance.overlayfs must be a boolean'):
            self.controller.validate_config()

    def test_toml_unit_rejects_missing_config_before_starting_host(self):
        (self.root / 'config/perga.toml').unlink()
        (self.root / 'config.env').write_text('PERGA_INSTANCE_NAME=demo\n')
        with self.assertRaisesRegex(ctl.DeployError, 'missing config/perga.toml'):
            self.controller.validate_config()
        host = self.root / 'current/host/Perga.Host'
        host.write_text('#!/bin/sh\necho started\n')
        result = subprocess.run(self.host_entrypoint(), capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('missing config/perga.toml', result.stderr)
        self.assertEqual(result.stdout, '')

    def test_install_start_order(self):
        fresh = ctl.Controller(self.base / 'fresh', name='demo', systemctl=self.systemctl)
        fresh.install(self.args(self.archive(self.old), no_start=False, unit_dir=str(self.base / 'fresh-units')))
        starts = [event for event in self.events if event[:2] == ('systemctl', 'start')]
        self.assertEqual(starts, [('systemctl', 'start', 'demo-term.service'),
                                  ('systemctl', 'start', 'demo.service')])

    def test_failed_install_does_not_leave_default_config_for_retry(self):
        fresh = ctl.Controller(self.base / 'fresh', name='demo', systemctl=self.systemctl)
        invalid = self.base / 'invalid.tar'
        invalid.write_text('invalid archive')
        unit_dir = str(self.base / 'fresh-units')
        with self.assertRaises(tarfile.ReadError):
            fresh.install(self.args(invalid, unit_dir=unit_dir))
        self.assertFalse((fresh.root / 'config/perga.toml').exists())

        host_url = 'http://127.0.0.1:4241'
        fresh.install(self.args(self.archive(self.old), unit_dir=unit_dir, host_url=host_url))
        self.assertEqual(fresh.validate_config().host_url, host_url)

    def test_adopt_keeps_data_inode_and_contents(self):
        root = self.base / 'flat'
        root.mkdir()
        import shutil
        for component in ctl.COMPONENTS:
            shutil.copytree(self.old / component, root / component)
        (root / 'data').mkdir()
        db = root / 'data/perga.db'
        db.write_bytes(b'')
        marker = root / 'data/workspace'
        marker.write_bytes(b'unchanged')
        before = (db.stat().st_ino, marker.stat().st_ino)
        adopted = ctl.Controller(root, 'demo', systemctl=self.systemctl)
        adopted.adopt(self.args())
        self.assertEqual(before, (db.stat().st_ino, marker.stat().st_ino))
        self.assertEqual(marker.read_bytes(), b'unchanged')
        self.assertTrue((root / 'host/Perga.Host').exists())
        self.assertTrue(adopted.current()[1]['releaseId'].startswith('adopted-'))

    def test_adopt_rejects_live_service(self):
        root = self.base / 'live-flat'
        (root / 'data').mkdir(parents=True)
        adopted = ctl.Controller(root, 'demo', systemctl=lambda args: subprocess.CompletedProcess(args, 0, 'active\n'))
        with self.assertRaisesRegex(ctl.DeployError, 'services stopped'):
            adopted.adopt(self.args())
        self.assertFalse((root / 'current').exists())

    def test_web_templates_only_reload_never_drains_or_restarts(self):
        candidate = self.bundle('r2', web='web-v2', templates='templates-v2')
        receipt = self.controller.apply(self.args(self.archive(candidate)))
        self.assertEqual(receipt['status'], 'succeeded')
        self.assertEqual(receipt['notifyTrackId'], 'track-1')
        self.assertEqual(os.readlink(self.root / 'previous'), 'releases/r1')
        self.assertEqual(os.readlink(self.root / 'current'), 'releases/r2')
        self.assertEqual([event[2] for event in self.events if event[0] == 'http'],
                         ['/api/v1/system/reload', '/healthz', '/api/v1/system/info'])
        self.assertFalse(any(event[0] == 'systemctl' for event in self.events))
        self.assertEqual(ctl.read_json(self.root / 'runtime/upgrade-result.json')['status'], 'succeeded')

    def test_host_upgrade_drains_before_stop_and_retains_bridge(self):
        self.create_db()
        candidate = self.bundle('r2', host='host-v2', schema=2)
        self.controller.apply(self.args(self.archive(candidate)))
        actions = [event[:3] for event in self.events]
        self.assertLess(actions.index(('http', 'POST', '/api/v1/system/drain')),
                        actions.index(('systemctl', 'stop', 'demo.service')))
        self.assertEqual([event for event in self.events if event[0] == 'systemctl'],
                         [('systemctl', 'stop', 'demo.service'), ('systemctl', 'start', 'demo.service')])
        self.assertEqual(ctl.schema_version(self.root / 'data/perga.db.bak-v1'), 1)
        self.assertEqual(ctl.read_json(self.root / 'runtime/upgrade-state.json')['phase'], 'succeeded')

    def test_drain_timeout_does_not_activate(self):
        self.busy = 1
        candidate = self.bundle('r2', host='host-v2')
        with self.assertRaisesRegex(ctl.DeployError, 'Drain timeout'):
            self.controller.apply(self.args(self.archive(candidate)))
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r1')
        self.assertEqual(ctl.read_json(self.root / 'runtime/upgrade-result.json')['status'], 'rolled-back')

    def test_force_allows_drain_timeout(self):
        self.busy = 1
        candidate = self.bundle('r2', host='host-v2')
        self.controller.apply(self.args(self.archive(candidate), force=True))
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r2')

    def test_bridge_change_requires_separate_permission(self):
        candidate = self.bundle('r2', term='term-v2')
        archive = self.archive(candidate)
        with self.assertRaisesRegex(ctl.DeployError, '--restart-term-bridge'):
            self.controller.apply(self.args(archive, force=True))
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r1')
        self.assertFalse(any(event[0] == 'systemctl' for event in self.events))
        self.controller.apply(self.args(archive, restart_term_bridge=True))
        self.assertIn(('systemctl', 'restart', 'demo-term.service'), self.events)

    def test_failure_rolls_back_and_restores_schema_backup(self):
        self.create_db()
        candidate = self.bundle('r2', host='host-v2', schema=2)
        self.fail_release, self.migrate_on_start = 'r2', True
        with self.assertRaisesRegex(ctl.DeployError, 'rolled back'):
            self.controller.apply(self.args(self.archive(candidate)))
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r1')
        self.assertEqual(ctl.schema_version(self.controller.db), 1)
        with sqlite3.connect(self.controller.db) as db:
            self.assertEqual(db.execute('SELECT value FROM content').fetchone()[0], 'old data')
        failed = list((self.root / 'data').glob('perga.db.failed-*'))
        self.assertEqual(len(failed), 1)
        self.assertEqual(ctl.schema_version(failed[0]), 2)
        receipt = ctl.read_json(self.root / 'runtime/upgrade-result.json')
        self.assertEqual(receipt['status'], 'rolled-back')
        self.assertIn('health verification failed', receipt['error'])
        actions = [event for event in self.events if event[0] == 'systemctl']
        self.assertEqual(actions, [('systemctl', 'stop', 'demo.service'), ('systemctl', 'start', 'demo.service'),
                                   ('systemctl', 'stop', 'demo.service'), ('systemctl', 'start', 'demo.service')])

    def test_failed_reload_restores_old_assets_without_restart(self):
        candidate = self.bundle('r2', web='web-v2')
        self.fail_release, self.fail_reload = 'r2', True
        with self.assertRaisesRegex(ctl.DeployError, 'rolled back'):
            self.controller.apply(self.args(self.archive(candidate)))
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r1')
        reloads = [event[3]['releaseId'] for event in self.events if event[:3] == ('http', 'POST', '/api/v1/system/reload')]
        self.assertEqual(reloads, ['r2', 'r1'])
        self.assertFalse(any(event[0] == 'systemctl' for event in self.events))

    def test_failed_recovery_blocks_new_apply(self):
        candidate = self.bundle('r2', host='host-v2')
        base_http = self.http
        def unavailable(method, path, body=None):
            if path == '/healthz':
                raise OSError('offline')
            return base_http(method, path, body)
        self.controller.http_override = unavailable
        with self.assertRaisesRegex(ctl.DeployError, 'rollback failed'):
            self.controller.apply(self.args(self.archive(candidate)))
        self.assertEqual(ctl.read_json(self.root / 'runtime/upgrade-result.json')['status'], 'recovery-required')
        with self.assertRaisesRegex(ctl.DeployError, 'unfinished upgrade'):
            self.controller.apply(self.args(self.archive(candidate)))
        self.controller.http_override = self.http
        self.assertEqual(self.controller.rollback(self.args())['status'], 'rolled-back')

    def test_manual_rollback(self):
        candidate = self.bundle('r2', web='web-v2')
        self.controller.apply(self.args(self.archive(candidate)))
        self.events.clear()
        receipt = self.controller.rollback(self.args())
        self.assertEqual(receipt['status'], 'rolled-back')
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r1')
        self.assertFalse(any(event[0] == 'systemctl' for event in self.events))

    def test_flock_contention_across_processes(self):
        code = ('import sys; sys.path.insert(0, sys.argv[1]); import perga_ctl; '
                'c=perga_ctl.Controller(sys.argv[2]); '
                '\nwith c.lock(): print("acquired")')
        with self.controller.lock():
            process = subprocess.run([sys.executable, '-c', code, str(MODULE.parent), str(self.root)], capture_output=True, text=True)
            self.assertNotEqual(process.returncode, 0)
            self.assertIn('another deployment holds', process.stderr)
        with self.controller.lock():
            pass

    def test_check_verifies_bytes_and_components(self):
        candidate = self.bundle('r2', web='web-v2')
        result = self.controller.check(self.args(self.archive(candidate)))
        self.assertEqual(result['changed'], ['web'])
        self.assertFalse(result['restartHost'])
        (candidate / 'host/Perga.Host').write_text('tampered')
        with self.assertRaisesRegex(ctl.DeployError, 'checksum mismatch'):
            self.controller.check(self.args(self.archive(candidate)))

    def test_old_web_build_id_rejected(self):
        candidate = self.bundle('r2', web='web-v2')
        manifest = ctl.read_json(candidate / 'manifest.json')
        manifest['webBuildId'] = ctl.read_json(self.old / 'manifest.json')['webBuildId']
        ctl.atomic_json(candidate / 'manifest.json', manifest)
        with self.assertRaisesRegex(ctl.DeployError, 'webBuildId'):
            self.controller.check(self.args(self.archive(candidate)))

    def test_tar_paths_and_links_rejected(self):
        for name, kind in [('../escape', tarfile.REGTYPE), ('/absolute', tarfile.REGTYPE),
                           ('link', tarfile.SYMTYPE), ('hard', tarfile.LNKTYPE)]:
            with self.subTest(name=name):
                archive = self.base / 'bad.tar'
                with tarfile.open(archive, 'w') as tar:
                    info = tarfile.TarInfo(name)
                    info.type, info.linkname = kind, '/tmp'
                    tar.addfile(info, io.BytesIO(b''))
                with tempfile.TemporaryDirectory() as dest:
                    with self.assertRaisesRegex(ctl.DeployError, 'unsafe archive'):
                        manifest_module.unpack(archive, dest)

    def test_release_url_requires_allowlist(self):
        with self.assertRaisesRegex(ctl.DeployError, 'allowlist'):
            with self.controller.candidate(self.args(from_release='https://example.com/payload', from_tarball=None)):
                pass

    def test_unsupported_newer_database_never_starts_old_code(self):
        self.create_db()
        with sqlite3.connect(self.controller.db) as db:
            db.execute('INSERT INTO schema_migrations VALUES (2)')
        self.controller.swap('r1', 'previous')
        with self.assertRaisesRegex(ctl.DeployError, 'backup is missing'):
            self.controller.rollback(self.args())
        self.assertFalse(any(event[:2] == ('systemctl', 'start') for event in self.events))

    def test_default_systemctl_allows_inactive_for_adoption(self):
        from unittest.mock import patch
        with patch.object(ctl, 'run_command', return_value=subprocess.CompletedProcess([], 3, 'inactive\n')) as run:
            controller = ctl.Controller(self.base / 'not-live', name='demo')
            self.assertEqual(controller.service('is-active').returncode, 3)
            run.assert_called_once_with(['systemctl', '--user', 'is-active', 'demo.service'], check=False)

    def test_templates_only_reload(self):
        candidate = self.bundle('r2', templates='templates-v2')
        self.controller.apply(self.args(self.archive(candidate)))
        self.assertFalse(any(event[0] == 'systemctl' for event in self.events))
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r2')

    def test_staging_error_has_terminal_receipt(self):
        candidate = self.bundle('r2')
        (candidate / 'web/index.html').write_text('tampered')
        with self.assertRaisesRegex(ctl.DeployError, 'checksum mismatch'):
            self.controller.apply(self.args(self.archive(candidate)))
        receipt = ctl.read_json(self.root / 'runtime/upgrade-result.json')
        self.assertEqual(receipt['status'], 'failed')
        self.assertEqual(receipt['notifyTrackId'], 'track-1')
        self.assertFalse(any(event[0] == 'systemctl' for event in self.events))

    def test_drain_timeout_resumes_without_stopping_host(self):
        self.busy = 1
        candidate = self.bundle('r2', host='host-v2')
        with self.assertRaisesRegex(ctl.DeployError, 'Drain timeout'):
            self.controller.apply(self.args(self.archive(candidate)))
        self.assertFalse(any(event[0] == 'systemctl' for event in self.events))
        self.assertFalse(self.draining)

    def test_identity_mismatch_rolls_back_even_when_healthz_is_ok(self):
        candidate = self.bundle('r2', web='web-v2')
        base_http = self.http
        def stale(method, path, body=None):
            result = base_http(method, path, body)
            if path.endswith('/info') and self.controller.current()[1]['releaseId'] == 'r2':
                result['webBuildId'] = 'stale'
            return result
        self.controller.http_override = stale
        with self.assertRaisesRegex(ctl.DeployError, 'identity/readiness mismatch'):
            self.controller.apply(self.args(self.archive(candidate)))
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r1')

    def test_restore_recovers_corrupt_database_and_preserves_sidecars(self):
        self.create_db()
        self.controller.backup(1)
        self.controller.db.write_bytes(b'corrupt failed database')
        Path(str(self.controller.db) + '-wal').write_bytes(b'failed wal')
        state = {'oldSchemaVersion': 1}
        self.controller.restore(1, state)
        self.assertEqual(ctl.schema_version(self.controller.db), 1)
        failed = list((self.root / 'data').glob('perga.db.failed-*-wal'))
        self.assertEqual(len(failed), 1)
        self.assertEqual(failed[0].read_bytes(), b'failed wal')
        self.assertFalse(Path(str(self.controller.db) + '-wal').exists())

    def test_detached_upgrade_and_rollback_use_transient_service_and_write_receipts(self):
        candidate = self.bundle('r2', web='web-v2')
        archive = self.archive(candidate)
        entered, release = threading.Event(), threading.Event()
        outer = self
        class Handler(BaseHTTPRequestHandler):
            def dispatch(self, method):
                body = None
                if method == 'POST':
                    body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                if self.path.endswith('/reload'):
                    entered.set()
                    release.wait(5)
                result = outer.http(method, self.path, body)
                self.send_response(200)
                self.end_headers()
                self.wfile.write(json.dumps(result).encode())
            def do_GET(self):
                self.dispatch('GET')
            def do_POST(self):
                self.dispatch('POST')
            def log_message(self, *args):
                pass
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        config = self.root / 'config/perga.toml'
        config.write_text(config.read_text().replace('http://127.0.0.1:4240',
                          'http://127.0.0.1:' + str(server.server_port)))
        fake_bin = self.base / 'fake-bin'
        fake_bin.mkdir()
        launcher = fake_bin / 'systemd-run'
        launcher.write_text("#!/usr/bin/env python3\nimport sys, subprocess\n"
                            "from pathlib import Path\n"
                            "args=sys.argv[1:]\n"
                            "Path(__file__).with_suffix('.args').write_text(repr(args))\n"
                            "log=next(a.split('append:',1)[1] for a in args if a.startswith('--property=StandardOutput='))\n"
                            "with open(log,'ab') as out:\n"
                            " subprocess.Popen(args[args.index('--')+1:], stdin=subprocess.DEVNULL, stdout=out, stderr=out, start_new_session=True)\n")
        launcher.chmod(0o755)
        env = dict(os.environ, PATH=str(fake_bin) + os.pathsep + os.environ['PATH'])
        try:
            for operation in ('upgrade', 'rollback'):
                entered.clear()
                release.clear()
                (self.root / 'runtime/upgrade-result.json').unlink(missing_ok=True)
                source_args = ['--from-tarball', str(archive)] if operation == 'upgrade' else []
                process = subprocess.run([sys.executable, str(MODULE), '--root' , str(self.root), '--name',
                                          'demo', operation] + source_args,
                                         capture_output=True, text=True, timeout=10, env=env)
                self.assertEqual(process.returncode, 0, process.stderr)
                result = json.loads(process.stdout)
                self.assertTrue(result['unit'].startswith('demo-' + operation + '-'))
                self.assertIn('--property=Type=exec', launcher.with_suffix('.args').read_text())
                self.assertTrue(entered.wait(5))
                with self.assertRaisesRegex(ctl.DeployError, 'another deployment'):
                    with self.controller.lock():
                        pass
                release.set()
                receipt_path = self.root / 'runtime/upgrade-result.json'
                deadline = time.monotonic() + 5
                while not receipt_path.exists() and time.monotonic() < deadline:
                    time.sleep(0.02)
                receipt = ctl.read_json(receipt_path)
                self.assertEqual(receipt['jobId'], result['jobId'])
                self.assertEqual(receipt['status'], 'succeeded' if operation == 'upgrade' else 'rolled-back')
        finally:
            release.set()
            server.shutdown()
            server.server_close()
            thread.join()

    def test_detached_rollback_launch_failure_preserves_recovery_state(self):
        prior = dict(phase='recovery-required', oldReleaseId='r1', oldSchemaVersion=1,
                     restoreRequired=True, hostStopped=True, restartTermBridge=False,
                     mode='restart', jobId='interrupted', changed=['host'])
        path = self.root / 'runtime/upgrade-state.json'
        ctl.atomic_json(path, prior)
        args = SimpleNamespace(command='rollback', name='demo', timeout=0,
                               notify_track_id='track', force=False, restart_term_bridge=False)
        with patch.object(systemd_module, 'run_command', side_effect=OSError('launch failed')):
            with self.assertRaisesRegex(OSError, 'launch failed'):
                systemd_module.launch_detached(self.controller, args)
        self.assertEqual(ctl.read_json(path), prior)

    def test_rollback_wait_recovers_an_interrupted_launch_reservation(self):
        new = self.bundle('r2', web='new-web')
        self.controller.apply(self.args(self.archive(new)))
        args = SimpleNamespace(command='rollback', name='demo', timeout=0,
                               notify_track_id='track', force=False, restart_term_bridge=False)
        with patch.object(systemd_module, 'run_command'):
            queued = systemd_module.launch_detached(self.controller, args)
        # A queued worker with the wrong ID must never consume another reservation.
        with self.assertRaisesRegex(ctl.DeployError, 'already queued'):
            self.controller.rollback(self.args(job_id='stale-worker'))
        self.assertEqual(ctl.read_json(self.root / 'runtime/upgrade-state.json')['jobId'], queued['jobId'])
        # Explicit synchronous recovery is possible even if the original worker died.
        receipt = self.controller.rollback(self.args())
        self.assertEqual(receipt['status'], 'rolled-back')
        self.assertEqual(receipt['notifyTrackId'], self.args().notify_track_id)
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r1')

    def test_rollback_wait_runs_synchronously_without_transient_launcher(self):
        new = self.bundle('r2', web='new-web')
        self.controller.apply(self.args(self.archive(new)))
        with patch.object(ctl, 'Controller', return_value=self.controller):
            with patch.object(systemd_module, 'run_command') as launcher:
                with contextlib.redirect_stdout(io.StringIO()) as output:
                    self.assertEqual(ctl.main(['rollback', '--wait']), 0)
                launcher.assert_not_called()
        self.assertEqual(json.loads(output.getvalue())['status'], 'rolled-back')

    def test_delayed_rollback_worker_cannot_reuse_consumed_reservation(self):
        self.controller.apply(self.args(self.archive(self.bundle('r2', web='web-v2'))))
        args = SimpleNamespace(command='rollback', name='demo', timeout=0,
                               notify_track_id='track', force=False, restart_term_bridge=False)
        with patch.object(systemd_module, 'run_command'):
            queued = systemd_module.launch_detached(self.controller, args)
        with patch.object(ctl, 'Controller', return_value=self.controller):
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(ctl.main(['rollback', '--wait']), 0)
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r1')
        self.controller.apply(self.args(self.archive(self.bundle('r3', web='web-v3'))))
        before = ctl.read_json(self.root / 'runtime/upgrade-state.json')
        self.events.clear()
        with self.assertRaises(ctl.DeployError):
            self.controller.rollback(self.args(job_id=queued['jobId']))
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r3')
        self.assertEqual(ctl.read_json(self.root / 'runtime/upgrade-state.json'), before)
        self.assertEqual(self.events, [])

    def test_rollback_worker_requires_matching_queued_rollback(self):
        self.controller.apply(self.args(self.archive(self.bundle('r2', web='web-v2'))))
        path = self.root / 'runtime/upgrade-state.json'
        for prior in (None, dict(jobId='J', operation='rollback', phase='rolled-back'),
                      dict(jobId='J', operation='upgrade', phase='queued'),
                      dict(jobId='other', operation='rollback', phase='queued', priorState=None)):
            with self.subTest(prior=prior):
                if prior is None:
                    path.unlink(missing_ok=True)
                else:
                    ctl.atomic_json(path, prior)
                with self.assertRaises(ctl.DeployError):
                    self.controller.rollback(self.args(job_id='J'))
                self.assertEqual(ctl.read_json(path) if path.exists() else None, prior)
                self.assertEqual(self.controller.current()[1]['releaseId'], 'r2')

    def test_install_copies_all_controller_modules(self):
        for module in MODULE.parent.glob('*.py'):
            self.assertEqual((self.root / 'bin' / module.name).read_bytes(), module.read_bytes())

    def test_manual_rollback_drain_timeout_leaves_current_running(self):
        candidate = self.bundle('r2', host='host-v2')
        self.controller.apply(self.args(self.archive(candidate)))
        self.events.clear()
        self.busy = 1
        with self.assertRaisesRegex(ctl.DeployError, 'Drain timeout'):
            self.controller.rollback(self.args())
        self.assertEqual(self.controller.current()[1]['releaseId'], 'r2')
        self.assertFalse(any(event[0] == 'systemctl' for event in self.events))
        self.assertEqual(ctl.read_json(self.root / 'runtime/upgrade-result.json')['status'], 'failed')

    def test_installer_and_installed_launcher_in_temp_root(self):
        fake_bin = self.base / 'install-bin'
        fake_bin.mkdir()
        systemctl = fake_bin / 'systemctl'
        systemctl.write_text('#!/bin/sh\nexit 0\n')
        systemctl.chmod(0o755)
        root = self.base / 'shell-install'
        env = dict(os.environ, PATH=str(fake_bin) + os.pathsep + os.environ['PATH'])
        result = subprocess.run([str(MODULE.parent / 'install.sh'), '--from-tarball',
                                 str(self.archive(self.old)), '--root', str(root), '--name', 'demo',
                                 '--unit-dir', str(self.base / 'shell-units'), '--no-start'],
                                capture_output=True, text=True, env=env, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        result = subprocess.run([str(root / 'bin/perga-ctl'), '--root', str(root), '--name', 'demo', 'check'],
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(json.loads(result.stdout)['valid'])

    def test_install_refuses_to_replace_an_existing_unit(self):
        fresh = ctl.Controller(self.base / 'fresh', name='demo', systemctl=self.systemctl)
        before = (self.base / 'units/demo.service').read_bytes()
        with self.assertRaisesRegex(ctl.DeployError, 'unit already exists'):
            fresh.install(self.args(self.archive(self.old)))
        self.assertEqual((self.base / 'units/demo.service').read_bytes(), before)

    def test_adopt_flat_publish_layout_preserves_sqlite_bytes(self):
        import shutil
        root = self.base / 'flat-publish'
        (root / 'data').mkdir(parents=True)
        shutil.copy2(self.old / 'host/Perga.Host', root / 'Perga.Host')
        shutil.copy2(self.old / 'termBridge/perga-term-bridge', root / 'perga-term-bridge')
        shutil.copytree(self.old / 'web', root / 'wwwroot')
        shutil.copytree(self.old / 'templates', root / 'templates')
        db = root / 'data/perga.db'
        with sqlite3.connect(db) as connection:
            connection.execute('PRAGMA journal_mode=WAL')
            connection.execute('CREATE TABLE schema_migrations(version INTEGER)')
            connection.execute('INSERT INTO schema_migrations VALUES (1)')
            connection.commit()
            before = {p.name: p.read_bytes() for p in (root / 'data').iterdir()}
            controller = ctl.Controller(root, 'demo', systemctl=self.systemctl)
            controller.adopt(self.args(unit_dir=str(self.base / 'flat-units')))
            self.assertEqual(before, {p.name: p.read_bytes() for p in (root / 'data').iterdir()})
        self.assertEqual(controller.current()[1]['schemaVersion'], 1)

    def test_from_repo_build_uses_temporary_copy_and_standard_commands(self):
        from unittest.mock import patch
        import shutil
        repo = self.base / 'source-repo'
        (repo / 'src/Perga.Host').mkdir(parents=True)
        (repo / 'src/Perga.Host/Perga.Host.csproj').write_text('<Project/>')
        (repo / 'src/Perga.Storage.Sqlite/Migrations').mkdir(parents=True)
        (repo / 'src/Perga.Storage.Sqlite/Migrations/0001_init.sql').write_text('-- migration')
        (repo / 'fe').mkdir()
        (repo / 'tools/perga-term-bridge').mkdir(parents=True)
        shutil.copytree(self.old / 'templates', repo / 'templates')
        commands = []
        def build(args, cwd=None):
            commands.append(args[:2])
            cwd = Path(cwd)
            self.assertNotEqual(cwd, repo)
            if args[0] == 'dotnet':
                shutil.copytree(self.old / 'host', Path(args[args.index('-o') + 1]))
            elif args[:2] == ['npm', 'run']:
                shutil.copytree(self.old / 'web', cwd / 'web/dist')
            elif args[0] == 'cargo':
                (cwd / 'target/release').mkdir(parents=True)
                shutil.copy2(self.old / 'termBridge/perga-term-bridge', cwd / 'target/release/perga-term-bridge')
        with patch.object(manifest_module, 'run_command', side_effect=build):
            with self.controller.candidate(self.args(from_repo=str(repo))) as (path, manifest):
                self.assertEqual(manifest['schemaVersion'], 1)
                self.assertEqual(ctl.verify_release(path), manifest)
        self.assertEqual(commands, [['dotnet', 'publish'], ['npm', 'ci'], ['npm', 'run'], ['cargo', 'build']])
        self.assertFalse((repo / 'fe/web/dist').exists())

    def test_doctor_reports_healthy_and_unfinished_intent(self):
        self.create_db()
        self.controller.systemctl = lambda args: subprocess.CompletedProcess(args, 0, 'active')
        self.assertTrue(self.controller.doctor()['healthy'])
        ctl.atomic_json(self.root / 'runtime/upgrade-state.json', {'phase': 'activating'})
        result = self.controller.doctor()
        self.assertFalse(result['healthy'])
        self.assertIn('unfinished upgrade: explicit rollback required', result['errors'])


class HttpTests(unittest.TestCase):
    def test_real_loopback_transport_and_redirect_rejection(self):
        requests = []
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path == '/redirect':
                    self.send_response(302)
                    self.send_header('Location', 'http://example.com/')
                    self.end_headers()
                    return
                requests.append(self.path)
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'{"status":"ok"}')
            def log_message(self, *args):
                pass
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            client = ctl.Http('http://127.0.0.1:' + str(server.server_port))
            self.assertEqual(client('GET', '/healthz'), {'status': 'ok'})
            self.assertEqual(requests, ['/healthz'])
            with self.assertRaisesRegex(ctl.DeployError, 'redirects'):
                client('GET', '/redirect')
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_owner_token_is_sent_and_unsafe_token_files_are_rejected(self):
        seen = []
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                seen.append(self.headers.get('Authorization'))
                self.send_response(202)
                self.end_headers()
                self.wfile.write(b'{"draining":true}')
            def log_message(self, *args):
                pass
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            with tempfile.TemporaryDirectory() as directory:
                token = Path(directory) / 'system-management-token'
                token.write_text('a' * 64)
                token.chmod(0o600)
                client = ctl.Http('http://127.0.0.1:' + str(server.server_port), token)
                self.assertTrue(client('POST', '/api/v1/system/drain', {'draining': True})['draining'])
                self.assertEqual(seen, ['Bearer ' + 'a' * 64])
                token.chmod(0o644)
                with self.assertRaisesRegex(ctl.DeployError, 'owner-only'):
                    client('POST', '/api/v1/system/drain', {'draining': True})
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_remote_host_url_rejected(self):
        for url in ('http://example.com', 'https://127.0.0.1', 'http://127.0.0.1/unsafe', 'http://x@localhost'):
            with self.assertRaises(ctl.DeployError):
                ctl.Http(url)


if __name__ == '__main__':
    unittest.main()
