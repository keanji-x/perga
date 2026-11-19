"""Release manifest creation, byte verification and safe archive extraction."""
import contextlib
import tempfile
import urllib.parse
import urllib.request
import uuid
import hashlib
import json
import os
from pathlib import Path
import shutil
import tarfile

from perga_platform import DeployError, NoRedirect, atomic_json, identifier, read_json, run_command, sync_dir

COMPONENTS = ('host', 'web', 'templates', 'termBridge')


def component_hash(directory):
    directory = Path(directory)
    if directory.is_symlink() or not directory.is_dir():
        raise DeployError('missing component directory: ' + str(directory))
    rows = []
    for path in sorted(directory.rglob('*'), key=lambda p: p.relative_to(directory).as_posix()):
        if path.is_symlink() or not (path.is_dir() or path.is_file()):
            raise DeployError('links and special files forbidden: ' + str(path))
        if path.is_file():
            digest = hashlib.sha256()
            with path.open('rb') as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                    digest.update(chunk)
            rows.append([path.relative_to(directory).as_posix(), digest.hexdigest()])
    return hashlib.sha256(json.dumps(rows, separators=(',', ':')).encode()).hexdigest()


def make_manifest(path, release_id, schema_version):
    hashes = {key: component_hash(Path(path) / key) for key in COMPONENTS}
    manifest = dict(formatVersion=1, releaseId=identifier(release_id),
                    schemaVersion=schema_version, webBuildId=hashes['web'], components=hashes)
    atomic_json(Path(path) / 'manifest.json', manifest)
    return manifest


def verify_release(path):
    path = Path(path)
    if (path / 'manifest.json').is_symlink():
        raise DeployError('manifest must be a regular file')
    manifest = read_json(path / 'manifest.json')
    if not isinstance(manifest, dict) or not isinstance(manifest.get('components'), dict):
        raise DeployError('invalid manifest object')
    identifier(manifest.get('releaseId'))
    if (type(manifest.get('formatVersion')) is not int or manifest['formatVersion'] != 1 or type(manifest.get('schemaVersion')) is not int
            or manifest['schemaVersion'] < 0 or not isinstance(manifest.get('webBuildId'), str)
            or not manifest['webBuildId'] or set(manifest.get('components', {})) != set(COMPONENTS)):
        raise DeployError('invalid manifest')
    for key in COMPONENTS:
        if manifest['components'][key] != component_hash(path / key):
            raise DeployError('checksum mismatch: ' + key)
    for executable in ('host/Perga.Host', 'termBridge/perga-term-bridge'):
        if not (path / executable).is_file() or not os.access(path / executable, os.X_OK):
            raise DeployError('missing executable: ' + executable)
    return manifest


def unpack(archive, destination):
    with tarfile.open(archive, 'r:*') as tar:
        seen, total = set(), 0
        for index, member in enumerate(tar):
            name = member.name
            relative = Path(name)
            if (relative.is_absolute() or '..' in relative.parts or not relative.parts
                    or name in seen or not (member.isfile() or member.isdir())):
                raise DeployError('unsafe archive member: ' + name)
            seen.add(name)
            total += member.size
            if index >= 100000 or total > 4 * 1024**3:
                raise DeployError('archive exceeds extraction limits')
            target = Path(destination) / relative
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with tar.extractfile(member) as source, target.open('xb') as out:
                    shutil.copyfileobj(source, out)
                target.chmod(0o755 if member.mode & 0o111 else 0o644)



class ReleaseStaging:
    @contextlib.contextmanager
    def candidate(self, args):
        with tempfile.TemporaryDirectory(prefix='perga-build-') as tmp:
            dest = Path(tmp) / 'release'
            dest.mkdir()
            if args.from_repo:
                self.build_repo(Path(args.from_repo).resolve(), dest)
            elif args.from_tarball:
                unpack(args.from_tarball, dest)
            elif args.from_release:
                url = args.from_release
                prefix = self.config().get('PERGA_RELEASE_URL_PREFIX', '')
                parsed = urllib.parse.urlsplit(url)
                if (not prefix.startswith('https://') or not prefix.endswith('/')
                        or not url.startswith(prefix) or parsed.scheme != 'https'
                        or parsed.username or parsed.password or parsed.fragment
                        or any(p == '..' for p in urllib.parse.unquote(parsed.path).split('/'))):
                    raise DeployError('release URL is outside configured HTTPS allowlist')
                opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
                archive = Path(tmp) / 'release.tar'
                with opener.open(url, timeout=30) as response, archive.open('wb') as output:
                    size = 0
                    while chunk := response.read(1024 * 1024):
                        size += len(chunk)
                        if size > 2 * 1024**3:
                            raise DeployError('download exceeds size limit')
                        output.write(chunk)
                unpack(archive, dest)
            else:
                raise DeployError('a release source is required')
            yield dest, verify_release(dest)


    def build_repo(self, source, dest):
        if not (source / 'src/Perga.Host/Perga.Host.csproj').is_file():
            raise DeployError('not a Perga source repository')
        # Build in a copy: npm/cargo/dotnet do not modify the source checkout.
        work = dest.parent / 'repo'
        shutil.copytree(source, work, ignore=shutil.ignore_patterns('.git', 'node_modules', 'target',
                                                                  'bin', 'obj', 'dist', 'data', 'runtime', '.claude'))
        run_command(['dotnet', 'publish', 'src/Perga.Host/Perga.Host.csproj', '-c', 'Release',
                     '-o', str(dest / 'host')], cwd=work)
        run_command(['npm', 'ci'], cwd=work / 'fe')
        run_command(['npm', 'run', 'build'], cwd=work / 'fe')
        run_command(['cargo', 'build', '--release', '--locked'], cwd=work / 'tools/perga-term-bridge')
        shutil.copytree(work / 'fe/web/dist', dest / 'web')
        shutil.copytree(work / 'templates', dest / 'templates')
        (dest / 'termBridge').mkdir()
        shutil.copy2(work / 'tools/perga-term-bridge/target/release/perga-term-bridge',
                     dest / 'termBridge/perga-term-bridge')
        version = max(int(p.name.split('_')[0]) for p in (work / 'src/Perga.Storage.Sqlite/Migrations').glob('*.sql'))
        make_manifest(dest, 'repo-' + uuid.uuid4().hex, version)


    def stage(self, source, manifest):
        releases = self.root / 'releases'
        releases.mkdir(exist_ok=True)
        target = releases / manifest['releaseId']
        if target.exists():
            if verify_release(target) != manifest:
                raise DeployError('release ID already exists with different contents')
            return
        tmp = releases / ('.stage-' + uuid.uuid4().hex)
        try:
            shutil.copytree(source, tmp)
            verify_release(tmp)
            # Persist staged bytes before publishing the directory entry.
            for path in tmp.rglob('*'):
                if path.is_file():
                    with path.open('rb') as stream:
                        os.fsync(stream.fileno())
            for path in sorted((p for p in tmp.rglob('*') if p.is_dir()), reverse=True):
                sync_dir(path)
            sync_dir(tmp)
            os.rename(tmp, target)
            sync_dir(releases)
        finally:
            if tmp.exists():
                shutil.rmtree(tmp)
