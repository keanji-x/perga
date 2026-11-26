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
        var command = BuildPythonCommand(SearchFilesScript, workspaceRoot, string.Empty,
            WorkspaceFileSearch.ScanLimit.ToString(CultureInfo.InvariantCulture),
            WorkspaceFileSearch.ScanTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(WorkspaceFileSearch.SkippedDirectories),
            JsonSerializer.Serialize(WorkspaceFileSearch.GitRepositoryOverrides));
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

        scan_limit = int(sys.argv[3])
        scan_timeout_seconds = int(sys.argv[4])
        skipped = set(json.loads(sys.argv[5]))
        git_env = dict(os.environ, LC_ALL="C", GIT_OPTIONAL_LOCKS="0")
        for key in json.loads(sys.argv[6]):
            git_env.pop(key, None)
        paths = []

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

        def add_file(path):
            if os.path.isabs(path):
                return
            full = os.path.join(real_root, path)
            if os.path.realpath(full) != full or not within_workspace(full):
                return
            try:
                if stat.S_ISREG(os.lstat(full).st_mode):
                    paths.append(display_path(full))
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
                    for i, raw_path in enumerate(git_paths(process.stdout)):
                        if i == scan_limit:
                            return True
                        try:
                            add_file(raw_path.decode("utf-8"))
                        except UnicodeDecodeError:
                            continue
                    if process.wait() == 0:
                        return False
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
                                    add_file(os.path.relpath(entry.path, real_root))
                            except (OSError, UnicodeEncodeError):
                                continue
                except OSError:
                    if directory == real_root:
                        raise
            return False

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
