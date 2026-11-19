#!/usr/bin/env python3
"""Start the Host for a managed installation."""

import argparse
import os
from pathlib import Path
import sys

from perga_config import TOML_PATH, legacy_overlayfs, read_toml
from perga_platform import DeployError


def toml_launch(root, config, term_socket):
    data = root / 'data'
    workspaces = Path(config.workspaces_dir or data / 'workspaces')
    if config.overlayfs:
        data = data.resolve()
        workspaces = workspaces.resolve()
    else:
        if not term_socket:
            term_socket = os.environ.get('PERGA_TERM_BRIDGE_URL', '').removeprefix('http+unix://')
        if not Path(term_socket).is_absolute():
            raise DeployError('managed Host requires an absolute --term-socket')
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
    if config.proxy_url is not None:
        exclusions = ','.join(dict.fromkeys(('localhost', '127.0.0.1', '::1', *config.no_proxy)))
        for key in ('http_proxy', 'https_proxy', 'no_proxy', 'all_proxy'):
            env.pop(key, None)
            env.pop(key.upper(), None)
        for key, value in (('http_proxy', config.proxy_url), ('https_proxy', config.proxy_url),
                           ('no_proxy', exclusions)):
            env[key] = env[key.upper()] = value
    if config.overlayfs:
        command = overlay_command(root / 'bin', data, workspaces, command)
    return command, env


def overlay_command(binaries, data, workspaces, command):
    return [sys.executable, str(binaries / 'perga-overlayfs-launch.py'),
            '--data-dir', str(data), '--workspaces', str(workspaces), '--', *command]


def legacy_launch(root):
    host = str(root / 'current/host/Perga.Host')
    env = dict(os.environ)
    if not legacy_overlayfs(env.get('PERGA_ENABLE_OVERLAYFS', '0')):
        return [host], env
    data = root / 'data'
    workspaces = env.get('Perga__WorkspacesRootDirectory') or data / 'workspaces'
    env.pop('PERGA_TERM_BRIDGE_URL', None)
    env.update(Perga__TerminalBridgeEndpoint='', Perga__AutoManageTerminalBridge='true',
               Perga__EagerStartTerminalBridge='true',
               Perga__TerminalBridgeExecutablePath=str(root / 'current/termBridge/perga-term-bridge'))
    return overlay_command(root / 'bin', data, workspaces, [host]), env


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--term-socket')
    parser.add_argument('--name')
    parser.add_argument('--require-toml', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    try:
        config_path = root / TOML_PATH
        if args.require_toml or config_path.exists():
            if not config_path.exists():
                raise DeployError('missing config/perga.toml for managed Host')
            config = read_toml(config_path)
            if args.name and config.name != args.name:
                raise DeployError('configuration name does not match the managed service')
            command, env = toml_launch(root, config, args.term_socket)
        else:
            command, env = legacy_launch(root)
        os.execve(command[0], command, env)
    except (DeployError, OSError, ValueError) as exc:
        raise SystemExit(str(exc)) from None


if __name__ == '__main__':
    main()
