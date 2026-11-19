"""Linux process and loopback HTTP adapters for perga-ctl."""
import json
import os
from pathlib import Path
import re
import tempfile
import subprocess
import urllib.parse
import urllib.request


SAFE_ID = re.compile(r'[A-Za-z0-9][A-Za-z0-9._-]{0,127}\Z')


class DeployError(RuntimeError):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise DeployError('HTTP redirects are forbidden')


class Http:
    def __init__(self, base, token_path=None):
        url = urllib.parse.urlsplit(base)
        if (url.scheme != 'http' or url.hostname not in ('127.0.0.1', 'localhost', '::1')
                or url.username or url.password or url.path not in ('', '/') or url.query or url.fragment):
            raise DeployError('Host URL must be loopback HTTP without credentials or path')
        self.token_path = token_path
        self.base = base.rstrip('/')
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def __call__(self, method, path, body=None):
        data = None if body is None else json.dumps(body).encode()
        headers = {'Content-Type': 'application/json'}
        if self.token_path is not None and self.token_path.exists():
            if self.token_path.is_symlink() or self.token_path.stat().st_mode & 0o077:
                raise DeployError('system management token must be owner-only')
            token = self.token_path.read_text().strip()
            if len(token) != 64 or any(c not in '0123456789abcdefABCDEF' for c in token):
                raise DeployError('invalid system management token')
            headers['Authorization'] = 'Bearer ' + token
        req = urllib.request.Request(self.base + path, data=data, method=method,
                                     headers=headers)
        with self.opener.open(req, timeout=5) as response:
            return json.load(response)


def command_env():
    # Build tools need HOME for SDK/toolchain resolution; no ambient provider secrets.
    keys = ('HOME', 'PATH', 'USER', 'LOGNAME', 'LANG', 'LC_ALL', 'XDG_RUNTIME_DIR',
            'DBUS_SESSION_BUS_ADDRESS', 'DOTNET_ROOT', 'CARGO_HOME', 'RUSTUP_HOME')
    return {key: os.environ[key] for key in keys if key in os.environ}


def run_command(args, cwd=None, check=True):
    return subprocess.run(args, cwd=cwd, env=command_env(), check=check,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)


def sync_dir(path):
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def atomic_json(path, value):
    path = Path(path)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix='.' + path.name)
    try:
        with os.fdopen(fd, 'w') as stream:
            json.dump(value, stream, indent=2, sort_keys=True)
            stream.write('\n')
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(tmp, path)
        sync_dir(path.parent)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)


def read_json(path):
    with open(path) as stream:
        return json.load(stream)


def identifier(value):
    if not isinstance(value, str) or not SAFE_ID.fullmatch(value) or value in ('.', '..'):
        raise DeployError('invalid identifier: ' + str(value))
    return value
