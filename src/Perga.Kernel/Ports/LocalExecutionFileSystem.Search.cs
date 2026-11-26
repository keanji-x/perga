using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Perga.Contracts;
using Perga.VcsKit;

namespace Perga.Kernel.Ports;

public sealed partial class LocalExecutionFileSystem
{
    private static readonly UTF8Encoding GitPathEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    public async Task<WorkspaceFileSearchResultDto> SearchWorkspaceFilesAsync(
        string workspaceRoot, string? query, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (root, _) = ResolveWorkspacePath(workspaceRoot, null);
        if (UseWorkspaceFileReader(() => WorkspaceFileReader.GetEntryKind(root)) != WorkspaceFileReader.EntryKind.Directory)
            throw KernelException.InvalidArgument("Workspace root is not a directory.");

        var paths = new List<string>();
        var filter = new WorkspaceFileSearch.Query(query);
        var scanLimit = filter.Literal.Length == 0 ? WorkspaceFileSearch.ScanLimit : WorkspaceFileSearch.QueryScanLimit;
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scan.CancelAfter(TimeSpan.FromSeconds(WorkspaceFileSearch.ScanTimeoutSeconds));
        bool truncated;
        try
        {
            truncated = await TrySearchGitFilesAsync(root, paths, filter, scanLimit, scan.Token).ConfigureAwait(false)
                ?? UseWorkspaceFileReader(() => WalkFiles(root, paths, filter, scanLimit, scan.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            truncated = true;
        }
        ct.ThrowIfCancellationRequested();
        return WorkspaceFileSearch.Rank(paths, truncated, query, limit);
    }

    private static async Task<bool?> TrySearchGitFilesAsync(
        string root, List<string> paths, WorkspaceFileSearch.Query filter, int scanLimit, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["LC_ALL"] = "C";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var key in WorkspaceFileSearch.GitRepositoryOverrides)
            start.Environment.Remove(key);

        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            return null;
        }

        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var output = process.StandardOutput.BaseStream;
            var buffer = new byte[8192];
            using var record = new MemoryStream();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            var directories = new Dictionary<string, bool>(StringComparer.FromComparison(comparison)) { [root] = true };
            var scanned = 0;
            int count;
            while ((count = await output.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            {
                for (var i = 0; i < count;)
                {
                    var next = Array.IndexOf(buffer, (byte)0, i, count - i);
                    var end = next < 0 ? count : next;
                    record.Write(buffer, i, end - i);
                    i = end + 1;
                    if (next < 0) continue;

                    ct.ThrowIfCancellationRequested();
                    if (++scanned > scanLimit) return true;
                    string path;
                    try { path = GitPathEncoding.GetString(record.GetBuffer(), 0, (int)record.Length); }
                    catch (DecoderFallbackException) { continue; } // Paths on the wire must be valid UTF-8.
                    finally { record.SetLength(0); }
                    if (Path.IsPathRooted(path)) continue;
                    var relative = EscapeWorkspaceRelativePath(path);
                    if (filter.Score(relative) >= 0 && IsSearchableGitFile(path))
                        paths.Add(relative);
                }
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode == 0) return scanned > WorkspaceFileSearch.ScanLimit;
            var errors = await stderr.ConfigureAwait(false);
            if (!errors.StartsWith("fatal: not a git repository", StringComparison.Ordinal))
                throw KernelException.PreconditionFailed($"Git could not list workspace files: {errors.Trim()}");
            paths.Clear();
            return null;

            bool IsSearchableGitFile(string path)
            {
                string full;
                try { full = Path.GetFullPath(path, root); }
                catch (ArgumentException) { return false; }
                return full.StartsWith(prefix, comparison)
                    && IsRealDirectory(Path.GetDirectoryName(full)!)
                    && GetSearchEntryKind(full) == WorkspaceFileReader.EntryKind.RegularFile;
            }

            // An index can still describe files under a directory replaced by a symlink.
            // Check each parent once, so siblings only pay for the leaf metadata lookup.
            bool IsRealDirectory(string path)
            {
                if (directories.TryGetValue(path, out var real)) return real;
                real = GetSearchEntryKind(path) == WorkspaceFileReader.EntryKind.Directory
                    && IsRealDirectory(Path.GetDirectoryName(path)!);
                directories.Add(path, real);
                return real;
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // The process exited after the check.
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
        }
    }

    private static bool WalkFiles(string root, List<string> paths, WorkspaceFileSearch.Query filter, int scanLimit, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        var scanned = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++scanned > scanLimit) return true;
                    var kind = GetSearchEntryKind(path);
                    if (kind == WorkspaceFileReader.EntryKind.Directory)
                    {
                        if (!WorkspaceFileSearch.SkippedDirectories.Contains(Path.GetFileName(path), StringComparer.Ordinal))
                            pending.Push(path);
                    }
                    else if (kind == WorkspaceFileReader.EntryKind.RegularFile)
                    {
                        var relative = DisplayWorkspacePath(root, path);
                        if (filter.Score(relative) >= 0)
                            paths.Add(relative);
                    }
                }
            }
            catch (Exception error) when (directory != root && error is IOException or UnauthorizedAccessException)
            {
                // A directory can vanish or be inaccessible by the time it is scanned.
            }
        }
        return scanned > WorkspaceFileSearch.ScanLimit;
    }

    private static WorkspaceFileReader.EntryKind GetSearchEntryKind(string path)
    {
        try
        {
            return WorkspaceFileReader.GetEntryKind(path, followSymlinks: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return WorkspaceFileReader.EntryKind.Other;
        }
    }
}
