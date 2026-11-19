"""User service installation and detached deployment job handoff."""
import os
from pathlib import Path
import re
import shutil
import sys
import uuid

from perga_platform import (DeployError, Http, command_env, read_json, run_command,
                            atomic_json)
from perga_sqlite import schema_version


class SystemdInstallation:
    def service(self, action, term=False):
        return self.systemctl([action, self.name + ('-term.service' if term else '.service')])


    def configure(self, args):
        if not re.fullmatch(r'/[A-Za-z0-9_./@+\-]+', str(self.root)):
            raise DeployError('installation root contains unsafe systemd path characters')
        unit_dir = Path(args.unit_dir).expanduser().absolute()
        unit_dir.mkdir(parents=True, exist_ok=True)
        config = self.root / 'config.env'
        expected = {'PERGA_INSTANCE_NAME': self.name, 'PERGA_DATA_DIR': str(self.root / 'data'),
                    'PERGA_STATIC_WEB_DIR': str(self.root / 'current/web'),
                    'PERGA_TEMPLATES_DIR': str(self.root / 'current/templates')}
        if config.exists():
            self.validate_config()
        else:
            Http(args.host_url)
            values = dict(expected, PERGA_HOST_URL=args.host_url, ASPNETCORE_URLS=args.host_url)
            fd = os.open(config, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            with os.fdopen(fd, 'w') as stream:
                stream.write(''.join(k + '=' + v + '\n' for k, v in values.items()))
        binaries = self.root / 'bin'
        binaries.mkdir(exist_ok=True)
        sources = list(Path(__file__).resolve().parent.glob('*.py'))
        sources.append(Path(__file__).resolve().parent / 'perga-ctl')
        for source in sources:
            target = binaries / source.name
            if source != target:
                shutil.copy2(source, target)
            target.chmod(0o755)
        common = '\nUMask=0077\nRestart=on-failure\n\n[Install]\nWantedBy=default.target\n'
        host = (f'[Unit]\nDescription=Perga Host ({self.name})\nWants={self.name}-term.service\n'
                f'After={self.name}-term.service\n\n[Service]\nType=simple\nWorkingDirectory={self.root}\n'
                f'EnvironmentFile={config}\nEnvironment=Perga__AutoManageTerminalBridge=false\n'
                f'Environment=PERGA_TERM_BRIDGE_URL=http+unix://%t/{self.name}/term.sock\n'
                f'ExecStart={self.root}/current/host/Perga.Host' + common)
        term = (f'[Unit]\nDescription=Perga terminal bridge ({self.name})\n\n[Service]\nType=simple\n'
                f'RuntimeDirectory={self.name}\nRuntimeDirectoryMode=0700\n'
                f'ExecStart={self.root}/current/termBridge/perga-term-bridge --listen-uds %t/{self.name}/term.sock' + common)
        for name, content in ((self.name + '.service', host), (self.name + '-term.service', term)):
            target = unit_dir / name
            if target.exists():
                backup = self.runtime / ('legacy-' + name)
                if not backup.exists():
                    shutil.copy2(target, backup)
            target.write_text(content)
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
