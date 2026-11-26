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
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scan.CancelAfter(TimeSpan.FromSeconds(WorkspaceFileSearch.ScanTimeoutSeconds));
        bool truncated;
        try
        {
            truncated = await TrySearchGitFilesAsync(root, paths, scan.Token).ConfigureAwait(false)
                ?? UseWorkspaceFileReader(() => WalkFiles(root, paths, scan.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            truncated = true;
        }
        ct.ThrowIfCancellationRequested();
        return WorkspaceFileSearch.Rank(paths, truncated, query, limit);
    }

    private static async Task<bool?> TrySearchGitFilesAsync(string root, List<string> paths, CancellationToken ct)
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
                    if (scanned++ == WorkspaceFileSearch.ScanLimit) return true;
                    string path;
                    try { path = GitPathEncoding.GetString(record.GetBuffer(), 0, (int)record.Length); }
                    catch (DecoderFallbackException) { continue; } // Paths on the wire must be valid UTF-8.
                    finally { record.SetLength(0); }
                    if (!Path.IsPathRooted(path) && IsSearchableFile(root, Path.Combine(root, path)))
                        paths.Add(EscapeWorkspaceRelativePath(path));
                }
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode == 0) return false;
            var errors = await stderr.ConfigureAwait(false);
            if (!errors.StartsWith("fatal: not a git repository", StringComparison.Ordinal))
                throw KernelException.PreconditionFailed($"Git could not list workspace files: {errors.Trim()}");
            paths.Clear();
            return null;
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

    private static bool WalkFiles(string root, List<string> paths, CancellationToken ct)
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
                    if (scanned++ == WorkspaceFileSearch.ScanLimit) return true;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!WorkspaceFileSearch.SkippedDirectories.Contains(Path.GetFileName(path), StringComparer.Ordinal))
                            pending.Push(path);
                    }
                    else if (IsSearchableFile(root, path))
                    {
                        paths.Add(DisplayWorkspacePath(root, path));
                    }
                }
            }
            catch (Exception error) when (directory != root && error is IOException or UnauthorizedAccessException)
            {
                // A directory can vanish or be inaccessible by the time it is scanned.
            }
        }
        return false;
    }

    private static bool IsSearchableFile(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        try
        {
            path = Path.GetFullPath(path);
            if (!path.StartsWith(prefix, comparison)) return false;
            var canonical = WorkspaceFileReader.ResolveExistingPath(path);
            return string.Equals(canonical, path, comparison) &&
                WorkspaceFileReader.GetEntryKind(canonical) == WorkspaceFileReader.EntryKind.RegularFile;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
