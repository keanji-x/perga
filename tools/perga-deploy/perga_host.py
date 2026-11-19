#!/usr/bin/env python3
"""Start the Host for a managed installation."""

import argparse
import os
from pathlib import Path
import sys

from perga_config import load_config
from perga_platform import DeployError


def build_launch(root, config, term_socket):
    if not Path(term_socket).is_absolute():
        raise DeployError('managed Host requires an absolute --term-socket')
    data = root / 'data'
    workspaces = Path(config.workspaces_dir or data / 'workspaces')
    if config.overlayfs:
        data = data.resolve()
        workspaces = workspaces.resolve()
    host = root / 'current/host/Perga.Host'
    settings = {
        'InstallationRoot': root,
        'InstallationName': config.name,
        'DataDir': data,
        'DbPath': data / 'perga.db',
        'WorkspacesRootDirectory': workspaces,
        'StaticWebRootDirectory': root / 'current/web',
        'TemplatesDirectory': root / 'current/templates',
        'AutoManageTerminalBridge': str(config.overlayfs).lower(),
        'EagerStartTerminalBridge': str(config.overlayfs).lower(),
        'TerminalBridgeEndpoint': '' if config.overlayfs else 'http+unix://' + term_socket,
        'TerminalBridgeExecutablePath': root / 'current/termBridge/perga-term-bridge',
        'CodexWorkerHomeDirectory': config.codex_worker_home,
    }
    settings.update((f'ProviderExecutables:{provider}', path)
                    for provider, path in config.provider_executables.items())
    command = [str(host), '--urls=' + config.host_url]
    command.extend(f'--Perga:{key}={value}' for key, value in settings.items())
    env = dict(os.environ)
    env.pop('PERGA_TERM_BRIDGE_URL', None)
    env['ASPNETCORE_URLS'] = config.host_url
    for key in ('http_proxy', 'https_proxy', 'no_proxy', 'all_proxy'):
        env.pop(key, None)
        env.pop(key.upper(), None)
    if config.proxy_url is not None:
        exclusions = ','.join(dict.fromkeys(('localhost', '127.0.0.1', '::1', *config.no_proxy)))
        for key, value in (('http_proxy', config.proxy_url), ('https_proxy', config.proxy_url),
                           ('no_proxy', exclusions)):
            env[key] = env[key.upper()] = value
    if config.overlayfs:
        command = [sys.executable, str(root / 'bin/perga-overlayfs-launch.py'),
                   '--data-dir', str(data), '--workspaces', str(workspaces), '--', *command]
    return command, env


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--term-socket', required=True)
    parser.add_argument('--name', required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    try:
        config = load_config(root, args.name)
        command, env = build_launch(root, config, args.term_socket)
        os.execve(command[0], command, env)
    except (DeployError, OSError, ValueError) as exc:
        raise SystemExit(str(exc)) from None


if __name__ == '__main__':
    main()
