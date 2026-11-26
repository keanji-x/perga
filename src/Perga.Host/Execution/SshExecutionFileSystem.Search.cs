using System.Globalization;
using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.Execution;

public sealed partial class SshExecutionFileSystem
{
    public async Task<WorkspaceFileSearchResultDto> SearchWorkspaceFilesAsync(
        string workspaceRoot, string? query, int limit, CancellationToken ct = default)
    {
        var filter = new WorkspaceFileSearch.Query(query);
        var command = BuildPythonCommand(SearchFilesScript, workspaceRoot, string.Empty,
            WorkspaceFileSearch.ScanLimit.ToString(CultureInfo.InvariantCulture),
            WorkspaceFileSearch.ScanTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(WorkspaceFileSearch.SkippedDirectories),
            JsonSerializer.Serialize(WorkspaceFileSearch.GitRepositoryOverrides),
            JsonSerializer.Serialize(new { literal = filter.Literal, groups = filter.AsciiPrefilterGroups() }),
            WorkspaceFileSearch.QueryScanLimit.ToString(CultureInfo.InvariantCulture));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var (exitCode, stdout, stderr) = await SshCommandBuilder.RunTextAsync(_config, command, ct: timeout.Token).ConfigureAwait(false);
            if (exitCode != 0)
                throw KernelException.PreconditionFailed($"Failed to search workspace on target '{_config.TargetId}': {stderr.Trim()}");

            using var doc = JsonDocument.Parse(stdout);
            var result = doc.RootElement;
            ThrowIfRemoteError(result);
            var paths = result.GetProperty("paths").EnumerateArray().Select(value => value.GetString()!);
            return WorkspaceFileSearch.Rank(paths, result.GetProperty("truncated").GetBoolean(), query, limit);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw KernelException.PreconditionFailed($"Workspace search timed out connecting to target '{_config.TargetId}'.");
        }
    }

    private const string SearchFilesScript = WorkspacePathScript + "\n" + """
        import signal, subprocess, tempfile

        if not stat.S_ISDIR(info.st_mode):
            fail("invalid_argument", "Workspace root is not a directory.")

        index_limit = int(sys.argv[3])
        scan_timeout_seconds = int(sys.argv[4])
        skipped = set(json.loads(sys.argv[5]))
        git_env = dict(os.environ, LC_ALL="C", GIT_OPTIONAL_LOCKS="0")
        for key in json.loads(sys.argv[6]):
            git_env.pop(key, None)
        prefilter = json.loads(sys.argv[7])
        literal = prefilter["literal"]
        scan_limit = int(sys.argv[8]) if literal else index_limit
        ascii_groups = [[term.upper() for term in group] for group in prefilter["groups"]]
        paths = []
        root_prefix = real_root.rstrip(os.sep) + os.sep
        directories = {real_root: True}

        class ScanTimeout(Exception):
            pass

        def stop_scan(*_):
            raise ScanTimeout()

        def git_paths(output):
            pending = b""
            while True:
                chunk = os.read(output.fileno(), 8192)
                if not chunk:
                    return
                *records, pending = (pending + chunk).split(b"\0")
                yield from records

        def may_match(path):
            # The Host owns ranking and Unicode matching. Drop only definite ASCII misses
            # before spending time on metadata or sending results back over SSH.
            if not ascii_groups or not path.isascii() or path == literal or path.rsplit("/", 1)[-1] == literal:
                return True
            upper = path.upper()
            def is_subsequence(term):
                chars = iter(upper)
                return all(char in chars for char in term)
            return all(any(is_subsequence(term) for term in group) for group in ascii_groups)

        def relative_path(path):
            return "./" + path if path == "~" or path.startswith("~/") else path

        def real_directory(path):
            if path not in directories:
                try:
                    directories[path] = (stat.S_ISDIR(os.lstat(path).st_mode)
                                         and real_directory(os.path.dirname(path)))
                except OSError:
                    directories[path] = False
            return directories[path]

        def add_git_file(path):
            relative = relative_path(path)
            if os.path.isabs(path) or not may_match(relative):
                return
            full = os.path.normpath(os.path.join(real_root, path))
            if not full.startswith(root_prefix) or full == real_root:
                return
            try:
                if real_directory(os.path.dirname(full)) and stat.S_ISREG(os.lstat(full).st_mode):
                    paths.append(relative)
            except OSError:
                pass

        def scan_git():
            with tempfile.TemporaryFile() as errors:
                try:
                    process = subprocess.Popen(
                        ["git", "-c", "core.fsmonitor=false", "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
                        cwd=real_root, env=git_env, stdout=subprocess.PIPE, stderr=errors)
                except FileNotFoundError:
                    return None

                try:
                    scanned = 0
                    for scanned, raw_path in enumerate(git_paths(process.stdout), start=1):
                        if scanned > scan_limit:
                            return True
                        try:
                            add_git_file(raw_path.decode("utf-8"))
                        except UnicodeDecodeError:
                            continue
                    if process.wait() == 0:
                        return scanned > index_limit
                    errors.seek(0)
                    error = errors.read(4096)
                    if not error.startswith(b"fatal: not a git repository"):
                        fail("precondition_failed", "Git could not list workspace files: " + error.decode("utf-8", "replace").strip())
                    paths.clear()
                    return None
                finally:
                    if process.poll() is None:
                        process.kill()
                    process.wait()
                    process.stdout.close()

        def walk_files():
            pending = [real_root]
            scanned = 0
            while pending:
                directory = pending.pop()
                try:
                    with os.scandir(directory) as entries:
                        for entry in entries:
                            if scanned == scan_limit:
                                return True
                            scanned += 1
                            try:
                                entry.name.encode("utf-8")
                                if entry.is_dir(follow_symlinks=False) and entry.name not in skipped:
                                    pending.append(entry.path)
                                elif entry.is_file(follow_symlinks=False):
                                    relative = relative_path(entry.path[len(root_prefix):])
                                    if may_match(relative):
                                        paths.append(relative)
                            except (OSError, UnicodeEncodeError):
                                continue
                except OSError:
                    if directory == real_root:
                        raise
            return scanned > index_limit

        signal.signal(signal.SIGALRM, stop_scan)
        signal.setitimer(signal.ITIMER_REAL, scan_timeout_seconds)
        try:
            truncated = scan_git()
            if truncated is None:
                truncated = walk_files()
        except ScanTimeout:
            truncated = True
        except OSError as error:
            path_error(error)
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
        print(json.dumps({"paths": paths, "truncated": truncated}))
        """;
}
