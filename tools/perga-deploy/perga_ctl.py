#!/usr/bin/env python3
"""User-scoped Perga release controller. Python 3 standard library, Linux only."""
import argparse
import contextlib
import fcntl
import json
import math
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import tarfile
import tempfile
import time
import uuid

from perga_platform import DeployError, Http, atomic_json, identifier, read_json, run_command, sync_dir
from perga_config import load_config, toml_reader
from perga_manifest import COMPONENTS, ReleaseStaging, make_manifest, verify_release
from perga_sqlite import DatabaseRecovery, schema_version, validate_database
from perga_systemd import SystemdInstallation, launch_detached

TERMINAL = {'succeeded', 'rolled-back', 'failed'}


class Controller(DatabaseRecovery, SystemdInstallation, ReleaseStaging):
    def __init__(self, root, name='perga', http=None, systemctl=None, timeout=60, sleep=time.sleep):
        self.root = Path(root).expanduser().absolute()
        self.name = identifier(name)
        self.runtime = self.root / 'runtime'
        self.db = self.root / 'data/perga.db'
        self.http_override = http
        self.systemctl = systemctl or (lambda args: run_command(
            ['systemctl', '--user'] + args, check=args[0] != 'is-active'))
        self.timeout = timeout
        self.sleep = sleep

    def init_runtime(self):
        self.root.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.runtime.mkdir(exist_ok=True, mode=0o700)

    @contextlib.contextmanager
    def lock(self):
        self.init_runtime()
        fd = os.open(self.runtime / 'upgrade.lock', os.O_CREAT | os.O_RDWR, 0o600)
        try:
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError as exc:
                raise DeployError('another deployment holds upgrade.lock') from exc
            yield fd
        finally:
            os.close(fd)

    def config(self):
        return load_config(self.root, self.name)

    def http(self, method, path, body=None):
        transport = self.http_override or Http(self.validate_config().host_url,
                                               self.runtime / 'system-management-token')
        return transport(method, path, body)

    def validate_config(self):
        config = self.config()
        if config is None:
            raise DeployError('missing managed installation configuration')
        return config

    def prepare_config(self, host_url):
        if self.config() is None:
            Http(host_url)
            toml_reader()

    def configure_services(self, args):
        with self.lock():
            self.idle()
            self.current()
            self.validate_config()
            self.configure(args)
            return self.status()

    def current(self, link='current'):
        target = (self.root / link).resolve(strict=True)
        if target.parent != (self.root / 'releases').resolve() or not (self.root / link).is_symlink():
            raise DeployError('invalid release link: ' + link)
        manifest = verify_release(target)
        if target.name != manifest['releaseId']:
            raise DeployError('release directory identity mismatch')
        return target, manifest

    def swap(self, release_id, link='current'):
        identifier(release_id)
        temp = self.root / ('.' + link + '-' + uuid.uuid4().hex)
        try:
            temp.symlink_to('releases/' + release_id)
            os.replace(temp, self.root / link)
            sync_dir(self.root)
        finally:
            temp.unlink(missing_ok=True)

    def state(self, value, phase):
        value['phase'] = phase
        atomic_json(self.runtime / 'upgrade-state.json', value)

    def idle(self, job_id=None):
        path = self.runtime / 'upgrade-state.json'
        if path.exists():
            state = read_json(path)
            if job_id and state.get('phase') == 'queued' and state.get('jobId') == job_id:
                return
            if state.get('phase') not in TERMINAL:
                raise DeployError('unfinished upgrade; run doctor and rollback before retrying')

    def install(self, args):
        with self.lock():
            if (self.root / 'current').exists() or (self.root / 'data').exists():
                raise DeployError('existing installation; use adopt or upgrade')
            if not re.fullmatch(r'/[A-Za-z0-9_./@+\-]+', str(self.root)):
                raise DeployError('installation root contains unsafe systemd path characters')
            self.prepare_config(args.host_url)
            for unit in (self.name + '.service', self.name + '-term.service'):
                if (Path(args.unit_dir).expanduser() / unit).exists():
                    raise DeployError('unit already exists; use adopt with stopped services')
            with self.candidate(args) as (source, manifest):
                self.stage(source, manifest)
                (self.root / 'data').mkdir(mode=0o700)
                self.swap(manifest['releaseId'])
                self.configure(args)
                return self.status()

    def adopt(self, args):
        with self.lock():
            if (self.root / 'current').is_symlink():
                raise DeployError('already adopted')
            if not (self.root / 'data').is_dir():
                raise DeployError('flat installation must have an existing data directory')
            if not re.fullmatch(r'/[A-Za-z0-9_./@+\-]+', str(self.root)):
                raise DeployError('installation root contains unsafe systemd path characters')
            self.prepare_config(args.host_url)
            for term in (False, True):
                result = self.systemctl(['is-active', self.name + ('-term.service' if term else '.service')])
                if result.returncode == 0 or result.stdout.strip() not in ('inactive', 'failed', 'unknown'):
                    raise DeployError('adopt requires both instance services stopped')
            with tempfile.TemporaryDirectory(prefix='perga-adopt-') as tmp:
                source = Path(tmp) / 'release'
                source.mkdir()
                # Even read-only SQLite can create WAL sidecars. Inspect a copy.
                copied_db = Path(tmp) / 'perga.db'
                for suffix in ('', '-wal', '-shm'):
                    if Path(str(self.db) + suffix).exists():
                        shutil.copy2(str(self.db) + suffix, str(copied_db) + suffix)
                if (self.root / 'host').is_dir():
                    shutil.copytree(self.root / 'host', source / 'host', symlinks=True)
                else:
                    (source / 'host').mkdir()
                    # Flat dotnet publish payload: do not sweep data or credentials.
                    for path in self.root.iterdir():
                        if path.is_file() and (path.name == 'Perga.Host' or path.suffix in ('.dll', '.json', '.so', '.pdb')):
                            shutil.copy2(path, source / 'host' / path.name, follow_symlinks=False)
                    if (self.root / 'runtimes').is_dir():
                        shutil.copytree(self.root / 'runtimes', source / 'host/runtimes', symlinks=True)
                for key, old in (('web', 'web' if (self.root / 'web').exists() else 'wwwroot'), ('templates', 'templates')):
                    shutil.copytree(self.root / old, source / key, symlinks=True)
                if (self.root / 'termBridge').is_dir():
                    shutil.copytree(self.root / 'termBridge', source / 'termBridge', symlinks=True)
                else:
                    (source / 'termBridge').mkdir()
                    shutil.copy2(self.root / 'perga-term-bridge', source / 'termBridge/perga-term-bridge', follow_symlinks=False)
                manifest = make_manifest(source, 'adopted-' + uuid.uuid4().hex, schema_version(copied_db))
                verify_release(source)
                self.stage(source, manifest)
                self.swap(manifest['releaseId'])
                self.configure(args)
            return self.status()

    def status(self):
        result = {'root': str(self.root), 'name': self.name}
        for link in ('current', 'previous'):
            path = self.root / link
            result[link] = os.readlink(path) if path.is_symlink() else None
        for name in ('upgrade-state', 'upgrade-result'):
            path = self.runtime / (name + '.json')
            result[name] = read_json(path) if path.exists() else None
        return result

    @staticmethod
    def diff(old, new):
        changes = [k for k in COMPONENTS if old['components'][k] != new['components'][k]]
        if 'web' in changes and old['webBuildId'] == new['webBuildId']:
            raise DeployError('changed web payload requires a new webBuildId')
        if new['schemaVersion'] < old['schemaVersion']:
            raise DeployError('use rollback for a schema support downgrade')
        return changes

    def check(self, args):
        _, old = self.current()
        if not any((args.from_repo, args.from_tarball, args.from_release)):
            return {'valid': True, 'manifest': old}
        with self.candidate(args) as (_, new):
            changed = self.diff(old, new)
            return {'releaseId': new['releaseId'], 'changed': changed,
                    'restartHost': bool(set(changed) - {'web', 'templates'}) or old['schemaVersion'] != new['schemaVersion'],
                    'restartTermBridge': 'termBridge' in changed}

    def drain(self, force):
        self.http('POST', '/api/v1/system/drain', {'draining': True})
        deadline = time.monotonic() + self.timeout
        while True:
            info = self.http('GET', '/api/v1/system/info')
            if info.get('draining') is True and type(info.get('activeDispatches')) is int and info['activeDispatches'] == 0:
                return
            if time.monotonic() >= deadline:
                if force:
                    return
                raise DeployError('Drain timeout; active dispatches did not settle')
            self.sleep(0.2)

    def health(self, manifest):
        deadline = time.monotonic() + self.timeout
        last = None
        while True:
            try:
                health = self.http('GET', '/healthz')
                info = self.http('GET', '/api/v1/system/info')
                if (health.get('status') == 'ok' and info.get('releaseId') == manifest['releaseId']
                        and info.get('webBuildId') == manifest['webBuildId']
                        and info.get('schemaVersion') == manifest['schemaVersion']
                        and info.get('draining') is False):
                    return
                last = 'identity/readiness mismatch'
            except (OSError, ValueError, DeployError) as exc:
                last = str(exc)
            if time.monotonic() >= deadline:
                raise DeployError('health verification failed: ' + str(last))
            self.sleep(0.2)

    def reload(self, manifest):
        self.http('POST', '/api/v1/system/reload',
                  {k: manifest[k] for k in ('releaseId', 'webBuildId')})
        self.health(manifest)

    def finish(self, state, phase, error=None, rollback_error=None):
        receipt = dict(state, status=phase, error=error, rollbackError=rollback_error, finishedAt=time.time())
        receipt.pop('phase', None)
        atomic_json(self.runtime / 'upgrade-result.json', receipt)
        self.state(state, phase)
        return receipt

    def recover(self, state, old):
        if state['mode'] == 'reload':
            self.swap(old['releaseId'])
            self.reload(old)
        elif state.get('hostStopped') is False:
            # A Drain timeout must not kill the busy work it was waiting for.
            self.http('POST', '/api/v1/system/drain', {'draining': False})
            self.health(old)
        else:
            self.service('stop')
            self.restore(old['schemaVersion'], state)
            self.swap(old['releaseId'])
            if state['restartTermBridge']:
                self.service('restart', term=True)
            self.service('start')
            self.health(old)

    def apply(self, args):
        with self.lock():
            self.idle(getattr(args, 'job_id', None))
            self.validate_config()
            _, old = self.current()
            state = dict(jobId=getattr(args, 'job_id', None) or uuid.uuid4().hex,
                         oldReleaseId=old['releaseId'], newReleaseId=None,
                         oldSchemaVersion=schema_version(self.db), changed=[],
                         notifyTrackId=args.notify_track_id, restartTermBridge=False,
                         mode='restart', hostStopped=False)
            self.state(state, 'staging')
            try:
                with self.candidate(args) as (source, new):
                    changed = self.diff(old, new)
                    if 'termBridge' in changed and not args.restart_term_bridge:
                        raise DeployError('termBridge changed; --restart-term-bridge is required')
                    if state['oldSchemaVersion'] > old['schemaVersion']:
                        raise DeployError('current database exceeds installed release schema support')
                    self.stage(source, new)
                    state.update(newReleaseId=new['releaseId'], changed=changed,
                                 restartTermBridge='termBridge' in changed,
                                 mode='restart' if set(changed) - {'web', 'templates'} or old['schemaVersion'] != new['schemaVersion'] else 'reload')
                self.state(state, 'prepared')
            except Exception as exc:
                self.finish(state, 'failed', str(exc))
                raise
            try:
                if state['mode'] == 'restart':
                    self.state(state, 'draining')
                    self.drain(args.force)
                    state['hostStopped'] = True
                    self.state(state, 'stopping-host')
                    self.service('stop')
                    state['oldSchemaVersion'] = schema_version(self.db)
                    if state['oldSchemaVersion'] > old['schemaVersion']:
                        raise DeployError('database changed beyond installed schema support')
                    self.backup(state['oldSchemaVersion'])
                self.state(state, 'activating')
                self.swap(old['releaseId'], 'previous')
                self.swap(new['releaseId'])
                if state['mode'] == 'reload':
                    self.reload(new)
                else:
                    if state['restartTermBridge']:
                        self.service('restart', term=True)
                    self.service('start')
                    self.health(new)
                return self.finish(state, 'succeeded')
            except Exception as exc:
                self.state(state, 'rolling-back')
                try:
                    self.recover(state, old)
                except Exception as rollback_exc:
                    self.finish(state, 'recovery-required', str(exc), str(rollback_exc))
                    raise DeployError(f'upgrade failed: {exc}; rollback failed: {rollback_exc}') from exc
                self.finish(state, 'rolled-back', str(exc))
                raise DeployError('upgrade failed and was rolled back: ' + str(exc)) from exc

    def rollback(self, args):
        with self.lock():
            self.validate_config()
            state_path = self.runtime / 'upgrade-state.json'
            prior = read_json(state_path) if state_path.exists() else None
            if getattr(args, 'job_id', None) is not None and not (
                    prior and prior.get('operation') == 'rollback'
                    and prior.get('phase') == 'queued' and prior.get('jobId') == args.job_id):
                raise DeployError('rollback reservation is missing, consumed, or another rollback is already queued')
            job_id = getattr(args, 'job_id', None) or uuid.uuid4().hex
            if prior and prior.get('operation') == 'rollback' and prior.get('phase') == 'queued':
                prior = prior['priorState']
                # Consume the launch reservation while holding the deployment lock.
                # Keep recovery evidence intact even if validation fails below.
                if prior is None:
                    state_path.unlink()
                    sync_dir(self.runtime)
                else:
                    atomic_json(state_path, prior)
            interrupted = prior is not None and prior.get('phase') not in TERMINAL
            if interrupted:
                old = verify_release(self.root / 'releases' / identifier(prior['oldReleaseId']))
                state = dict(prior)
                state['jobId'] = job_id
                state['notifyTrackId'] = args.notify_track_id
                # recovery never drains or waits for a Host that may be down.
            else:
                _, current = self.current()
                _, old = self.current('previous')
                changes = [k for k in COMPONENTS if current['components'][k] != old['components'][k]]
                if 'termBridge' in changes and not args.restart_term_bridge:
                    raise DeployError('rollback needs --restart-term-bridge')
                old_schema = old['schemaVersion']
                if prior and prior['oldReleaseId'] == old['releaseId']:
                    old_schema = prior['oldSchemaVersion']
                restart = (bool(set(changes) - {'web', 'templates'})
                           or current['schemaVersion'] != old['schemaVersion']
                           or schema_version(self.db) > old['schemaVersion'])
                state = dict(jobId=job_id, oldReleaseId=old['releaseId'],
                             newReleaseId=current['releaseId'], oldSchemaVersion=old_schema,
                             changed=changes, notifyTrackId=args.notify_track_id,
                             restartTermBridge='termBridge' in changes, hostStopped=True,
                             mode='restart' if restart else 'reload')
            if state['restartTermBridge'] and not args.restart_term_bridge:
                raise DeployError('recovery needs --restart-term-bridge')
            self.state(state, 'rolling-back')
            try:
                if not interrupted and state['mode'] == 'restart':
                    state['hostStopped'] = False
                    self.state(state, 'draining-rollback')
                    try:
                        self.drain(args.force)
                    except Exception as exc:
                        # Abort back to the still-running current release.
                        state.update(oldReleaseId=current['releaseId'], restartTermBridge=False)
                        try:
                            self.recover(state, current)
                        except Exception as resume_exc:
                            self.finish(state, 'recovery-required', str(exc), str(resume_exc))
                            raise
                        self.finish(state, 'failed', str(exc))
                        raise
                    state['hostStopped'] = True
                    self.state(state, 'rolling-back')
                self.recover(state, old)
                return self.finish(state, 'rolled-back')
            except Exception as exc:
                if state.get('phase') not in ('failed', 'recovery-required'):
                    self.finish(state, 'recovery-required', str(exc))
                raise

    def doctor(self):
        errors = []
        for label, fn in [('config', self.validate_config), ('current', self.current),
                          ('database', lambda: validate_database(self.db)),
                          ('health', lambda: self.health(self.current()[1]))]:
            try:
                fn()
            except Exception as exc:
                errors.append(label + ': ' + str(exc))
        if (self.root / 'previous').is_symlink():
            try:
                self.current('previous')
            except Exception as exc:
                errors.append('previous: ' + str(exc))
        for term in (False, True):
            try:
                if self.service('is-active', term).returncode != 0:
                    raise DeployError('unit is not active')
            except Exception as exc:
                errors.append('service: ' + str(exc))
        state = self.status()['upgrade-state']
        if state and state.get('phase') not in TERMINAL:
            errors.append('unfinished upgrade: explicit rollback required')
        return {'healthy': not errors, 'errors': errors}


def parser():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root', default='~/.local/share/perga')
    p.add_argument('--name', default='perga')
    p.add_argument('--timeout', type=float, default=60)
    sub = p.add_subparsers(dest='command', required=True)
    for name in ('install', 'adopt', 'configure-services', 'status', 'check', 'upgrade', 'apply', 'rollback', 'doctor'):
        command = sub.add_parser(name)
        for option, kind in (('root', str), ('name', str), ('timeout', float)):
            command.add_argument('--' + option, type=kind, default=argparse.SUPPRESS)
        if name in ('install', 'check', 'upgrade', 'apply'):
            sources = command.add_mutually_exclusive_group(required=name != 'check')
            for source in ('tarball', 'repo', 'release'):
                sources.add_argument('--from-' + source)
        if name in ('install', 'adopt', 'configure-services'):
            command.add_argument('--unit-dir', default='~/.config/systemd/user')
        if name in ('install', 'adopt'):
            command.add_argument('--host-url', default='http://127.0.0.1:4240')
            command.add_argument('--no-start', action='store_true')
        if name == 'configure-services':
            command.set_defaults(no_start=True)
        if name in ('upgrade', 'apply', 'rollback'):
            command.add_argument('--force', action='store_true')
            command.add_argument('--restart-term-bridge', action='store_true')
            command.add_argument('--notify-track-id')
        if name in ('upgrade', 'rollback'):
            command.add_argument('--wait', action='store_true')
        if name in ('apply', 'rollback'):
            command.add_argument('--job-id', help=argparse.SUPPRESS)
    return p


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        ctl = Controller(args.root, args.name, timeout=args.timeout)
        if not math.isfinite(args.timeout) or args.timeout < 0:
            raise DeployError('timeout must be nonnegative')
        if args.command in ('upgrade', 'rollback') and not args.wait:
            result = launch_detached(ctl, args)
        elif args.command in ('upgrade', 'apply'):
            result = ctl.apply(args)
        elif args.command in ('status', 'doctor'):
            result = getattr(ctl, args.command)()
        else:
            result = getattr(ctl, args.command.replace('-', '_'))(args)
        print(json.dumps(result, indent=2))
        return 1 if args.command == 'doctor' and not result['healthy'] else 0
    except (DeployError, OSError, ValueError, sqlite3.Error, subprocess.SubprocessError, tarfile.TarError) as exc:
        print(json.dumps({'error': str(exc)}), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
