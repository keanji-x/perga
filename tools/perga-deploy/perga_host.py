#!/usr/bin/env python3
"""Start the Host for a managed installation."""

import os
from pathlib import Path
import sys


def main():
    binaries = Path(__file__).resolve().parent
    root = binaries.parent
    host = str(root / 'current/host/Perga.Host')
    overlayfs = os.environ.get('PERGA_ENABLE_OVERLAYFS', '0')
    if overlayfs == '0':
        os.execv(host, [host])
    if overlayfs != '1':
        raise SystemExit('PERGA_ENABLE_OVERLAYFS must be 0 or 1')

    data = root / 'data'
    workspaces = os.environ.get('Perga__WorkspacesRootDirectory') or str(data / 'workspaces')
    env = dict(os.environ)
    env.pop('PERGA_TERM_BRIDGE_URL', None)
    env.update(Perga__TerminalBridgeEndpoint='', Perga__AutoManageTerminalBridge='true',
               Perga__EagerStartTerminalBridge='true',
               Perga__TerminalBridgeExecutablePath=str(root / 'current/termBridge/perga-term-bridge'))
    os.execve(sys.executable, [
        sys.executable, str(binaries / 'perga-overlayfs-launch.py'),
        '--data-dir', str(data), '--workspaces', workspaces, '--', host,
    ], env)


if __name__ == '__main__':
    main()
