"""Read managed installation settings once for both deployment and launch."""

from __future__ import annotations

from dataclasses import dataclass, field
import json
import os
from pathlib import Path
from urllib.parse import urlsplit

from perga_platform import DeployError, Http, identifier


TOML_PATH = Path('config/perga.toml')
LEGACY_PATH = Path('config.env')
PROVIDERS = ('agy', 'codex', 'opencode', 'claude')


@dataclass
class InstallationConfig:
    name: str
    host_url: str
    overlayfs: bool = False
    release_url_prefix: str = ''
    workspaces_dir: str | None = None
    provider_executables: dict[str, str] = field(default_factory=dict)
    codex_worker_home: str = ''
    proxy_url: str | None = None
    no_proxy: tuple[str, ...] = ()
    legacy: bool = False


def toml_reader():
    try:
        import tomllib
    except ImportError:
        raise DeployError('TOML installation configuration requires Python 3.11 or later') from None
    return tomllib


def table(value, name, allowed):
    if not isinstance(value, dict):
        raise DeployError(f'{name} must be a TOML table')
    unknown = value.keys() - set(allowed)
    if unknown:
        raise DeployError(f'unknown configuration key: {name}.{min(unknown)}')
    return value


def string(value, name):
    if not isinstance(value, str) or not value.strip() or any(ord(c) < 32 for c in value):
        raise DeployError(f'{name} must be a non-empty string without control characters')
    return value


def absolute_path(value, name):
    value = string(value, name)
    if not Path(value).is_absolute():
        raise DeployError(f'{name} must be an absolute path')
    return value


def read_toml(path):
    toml = toml_reader()
    if path.stat().st_mode & 0o077:
        raise DeployError('config/perga.toml must be owner-only (chmod 600)')
    with path.open('rb') as stream:
        try:
            values = toml.load(stream)
        except (toml.TOMLDecodeError, UnicodeError):
            raise DeployError('invalid TOML in config/perga.toml') from None
    table(values, 'perga.toml', ('instance', 'providers', 'proxy'))
    instance = table(values.get('instance', {}), 'instance',
                     ('name', 'host_url', 'overlayfs', 'release_url_prefix', 'workspaces_dir'))
    config = InstallationConfig(identifier(instance.get('name')),
                                string(instance.get('host_url'), 'instance.host_url'))
    Http(config.host_url)
    config.overlayfs = instance.get('overlayfs', False)
    if not isinstance(config.overlayfs, bool):
        raise DeployError('instance.overlayfs must be a boolean')
    if 'release_url_prefix' in instance:
        config.release_url_prefix = string(instance['release_url_prefix'], 'instance.release_url_prefix')
    if 'workspaces_dir' in instance:
        config.workspaces_dir = absolute_path(instance['workspaces_dir'], 'instance.workspaces_dir')

    providers = table(values.get('providers', {}), 'providers', PROVIDERS)
    for provider, options in providers.items():
        section = f'providers.{provider}'
        table(options, section, ('executable', 'worker_home') if provider == 'codex' else ('executable',))
        if 'executable' in options:
            config.provider_executables[provider] = absolute_path(options['executable'], section + '.executable')
        if 'worker_home' in options:
            config.codex_worker_home = absolute_path(options['worker_home'], section + '.worker_home')

    if 'proxy' in values:
        proxy = table(values['proxy'], 'proxy', ('url', 'no_proxy'))
        config.proxy_url = string(proxy.get('url'), 'proxy.url')
        try:
            parsed = urlsplit(config.proxy_url)
            if (parsed.scheme not in ('http', 'https') or not parsed.hostname or parsed.port == 0
                    or parsed.path not in ('', '/') or parsed.query or parsed.fragment):
                raise ValueError()
        except ValueError:
            raise DeployError('proxy.url must be an HTTP(S) proxy URL') from None
        exclusions = proxy.get('no_proxy', [])
        if not isinstance(exclusions, list):
            raise DeployError('proxy.no_proxy must be an array of strings')
        config.no_proxy = tuple(string(value, 'proxy.no_proxy entry') for value in exclusions)
    return config


def read_legacy(root):
    values = {}
    expected = {'PERGA_DATA_DIR': str(root / 'data'),
                'PERGA_STATIC_WEB_DIR': str(root / 'current/web'),
                'PERGA_TEMPLATES_DIR': str(root / 'current/templates')}
    allowed = expected.keys() | {'PERGA_INSTANCE_NAME', 'PERGA_HOST_URL', 'PERGA_RELEASE_URL_PREFIX',
                                 'PERGA_ENABLE_OVERLAYFS', 'ASPNETCORE_URLS'}
    for raw in (root / LEGACY_PATH).read_text().splitlines():
        if not raw.strip() or raw.lstrip().startswith('#'):
            continue
        key, sep, value = raw.partition('=')
        if not sep or key not in allowed or any(c in value for c in '\r\n\x00'):
            raise DeployError('unsupported config.env entry: ' + key)
        values[key] = value
    if any(values.get(key) != value for key, value in expected.items()):
        raise DeployError('config.env does not match managed paths')
    host_url = values.get('PERGA_HOST_URL', '')
    Http(host_url)
    if values.get('ASPNETCORE_URLS') != host_url:
        raise DeployError('Host listen URL and management URL must match')
    overlayfs = legacy_overlayfs(values.get('PERGA_ENABLE_OVERLAYFS', '0'))
    return InstallationConfig(identifier(values.get('PERGA_INSTANCE_NAME')), host_url, overlayfs,
                              values.get('PERGA_RELEASE_URL_PREFIX', ''), legacy=True)


def legacy_overlayfs(value):
    if value not in ('0', '1'):
        raise DeployError('PERGA_ENABLE_OVERLAYFS must be 0 or 1')
    return value == '1'


def load_config(root, name):
    if (root / TOML_PATH).exists():
        config = read_toml(root / TOML_PATH)
    elif (root / LEGACY_PATH).exists():
        config = read_legacy(root)
    else:
        return None
    if config.name != name:
        raise DeployError('configuration name does not match the managed instance')
    return config


def write_default_config(root, name, host_url):
    path = root / TOML_PATH
    path.parent.mkdir(mode=0o700, exist_ok=True)
    fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(fd, 'w', encoding='utf-8') as stream:
        stream.write(f'[instance]\nname = {json.dumps(name)}\nhost_url = {json.dumps(host_url)}\n')
