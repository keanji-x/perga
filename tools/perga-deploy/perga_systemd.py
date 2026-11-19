"""User service installation and detached deployment job handoff."""
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile
import uuid

from perga_platform import (DeployError, command_env, read_json, run_command,
                            atomic_json, sync_dir)
from perga_sqlite import schema_version
from perga_config import LEGACY_PATH, write_default_config


def replace_file(path, content, mode):
    fd, temp = tempfile.mkstemp(dir=path.parent, prefix='.' + path.name + '-')
    try:
        with os.fdopen(fd, 'wb') as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp, path)
        sync_dir(path.parent)
    finally:
        Path(temp).unlink(missing_ok=True)


class SystemdInstallation:
    def service(self, action, term=False):
        return self.systemctl([action, self.name + ('-term.service' if term else '.service')])


    def configure(self, args):
        if not re.fullmatch(r'/[A-Za-z0-9_./@+\-]+', str(self.root)):
            raise DeployError('installation root contains unsafe systemd path characters')
        unit_dir = Path(args.unit_dir).expanduser().absolute()
        unit_dir.mkdir(parents=True, exist_ok=True)
        if self.config() is None:
            write_default_config(self.root, self.name, args.host_url)
        config = self.validate_config()
        binaries = self.root / 'bin'
        binaries.mkdir(exist_ok=True)
        tools = Path(__file__).resolve().parent
        sources = {path.name: path for path in tools.glob('*.py')}
        sources['perga-ctl'] = tools / 'perga-ctl'
        launcher = tools.parent / 'perga-overlayfs' / 'launch.py'
        if launcher.is_file():
            sources['perga-overlayfs-launch.py'] = launcher
        # Older services can restart while their stable entry points are refreshed.
        # Publish the shared parser before replacing scripts that import it.
        order = ['perga_config.py', *sorted(sources.keys() - {'perga_config.py', 'perga_ctl.py'}), 'perga_ctl.py']
        for name in order:
            source = sources[name]
            target = binaries / name
            if source != target:
                replace_file(target, source.read_bytes(), 0o755)
        common = '\nUMask=0077\nRestart=on-failure\n\n[Install]\nWantedBy=default.target\n'
        legacy_environment = (f'EnvironmentFile={self.root / LEGACY_PATH}\n'
                              'Environment=Perga__AutoManageTerminalBridge=false\n'
                              f'Environment=PERGA_TERM_BRIDGE_URL=http+unix://%t/{self.name}/term.sock\n'
                              if config.legacy else '')
        require_toml = '' if config.legacy else ' --require-toml'
        host = (f'[Unit]\nDescription=Perga Host ({self.name})\nWants={self.name}-term.service\n'
                f'After={self.name}-term.service\n\n[Service]\nType=simple\nWorkingDirectory={self.root}\n'
                f'{legacy_environment}'
                f'ExecStart=/usr/bin/python3 {binaries}/perga_host.py --name {self.name} '
                f'--term-socket %t/{self.name}/term.sock{require_toml}' + common)
        term = (f'[Unit]\nDescription=Perga terminal bridge ({self.name})\n\n[Service]\nType=simple\n'
                f'RuntimeDirectory={self.name}\nRuntimeDirectoryMode=0700\n'
                f'ExecStart={self.root}/current/termBridge/perga-term-bridge --listen-uds %t/{self.name}/term.sock' + common)
        for name, content in ((self.name + '.service', host), (self.name + '-term.service', term)):
            target = unit_dir / name
            if target.exists():
                backup = self.runtime / ('legacy-' + name)
                if not backup.exists():
                    shutil.copy2(target, backup)
            replace_file(target, content.encode('utf-8'), 0o644)
        self.systemctl(['daemon-reload'])
        self.systemctl(['enable', self.name + '-term.service', self.name + '.service'])
        if not args.no_start:
            self.service('start', term=True)
            self.service('start')


def launch_detached(ctl, args):
    """Reserve a job before launching it outside the Host service's cgroup.

    Rollback keeps the interrupted recovery record inside the reservation. A
    launcher failure restores that record so recovery can be retried safely.
    """
    operation = args.command
    with ctl.lock():
        ctl.validate_config()
        state_path = ctl.runtime / 'upgrade-state.json'
        prior = read_json(state_path) if state_path.exists() else None
        if prior and prior.get('operation') == 'rollback' and prior.get('phase') == 'queued':
            raise DeployError('a rollback is already queued; use rollback --wait for explicit recovery')
        if operation == 'upgrade':
            ctl.idle()
            _, old = ctl.current()
        job_id = uuid.uuid4().hex
        state = dict(jobId=job_id, operation=operation, priorState=prior)
        if operation == 'upgrade':
            state.update(oldReleaseId=old['releaseId'], newReleaseId=None,
                         oldSchemaVersion=schema_version(ctl.db), mode='restart',
                         hostStopped=False, restartTermBridge=False, changed=[],
                         notifyTrackId=args.notify_track_id)
        ctl.state(state, 'queued')

    command = [sys.executable, str(Path(__file__).resolve().with_name('perga_ctl.py')),
               '--root', str(ctl.root), '--name', args.name, '--timeout', str(args.timeout),
               'apply' if operation == 'upgrade' else 'rollback', '--job-id', job_id]
    if operation == 'rollback':
        command.append('--wait')
    for key in ('from_tarball', 'from_repo', 'from_release', 'notify_track_id'):
        value = getattr(args, key, None)
        if value:
            if key in ('from_tarball', 'from_repo'):
                value = str(Path(value).expanduser().resolve())
            command.extend(['--' + key.replace('_', '-'), value])
    for key in ('force', 'restart_term_bridge'):
        if getattr(args, key):
            command.append('--' + key.replace('_', '-'))
    log = str(ctl.runtime / 'upgrade.log')
    unit = args.name + '-' + operation + '-' + job_id
    try:
        run_command(['systemd-run', '--user', '--collect', '--unit=' + unit,
                     '--property=Type=exec', '--property=UMask=0077',
                     '--property=StandardOutput=append:' + log,
                     '--property=StandardError=append:' + log, '--', '/usr/bin/env', '-i'] +
                    [key + '=' + value for key, value in command_env().items()] + command)
    except Exception as exc:
        with ctl.lock():
            saved = read_json(state_path)
            if saved.get('jobId') == job_id and saved.get('phase') == 'queued':
                if operation == 'rollback':
                    if prior is None:
                        state_path.unlink()
                    else:
                        atomic_json(state_path, prior)
                else:
                    ctl.finish(state, 'failed', str(exc))
        raise
    return {'jobId': job_id, 'unit': unit, 'log': log}
