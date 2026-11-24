namespace Perga.MemoryKit;

internal sealed class MemoryDirectory
{
    private const int MaxReadBytes = 1024 * 1024;
    private const FileAttributes UnsafeDirectoryAttributes = FileAttributes.ReparsePoint | FileAttributes.Device;
    private const FileAttributes UnsafeFileAttributes = UnsafeDirectoryAttributes | FileAttributes.Directory;
    private readonly string _root;

    internal MemoryDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    internal string ScopePath(MemoryWriteScope scope) => scope.Kind switch
    {
        MemoryScopeKind.Area => AreaPath(scope.AreaId),
        MemoryScopeKind.Global when scope.AreaId is null => ChildPath(_root, "global"),
        _ => throw new MemoryValidationException("A write must target Global or exactly one Area."),
    };

    internal ScopeDirectory? OpenScope(string directory, bool create = false)
    {
        if (CheckScopeDirectory(directory))
            return new(this, directory);
        if (!create)
            return null;

        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
        {
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(_root, ownerOnly);
            var parent = Path.GetDirectoryName(directory)!;
            if (parent != _root)
                Directory.CreateDirectory(parent, ownerOnly);
            Directory.CreateDirectory(directory, ownerOnly);
        }
        return CheckScopeDirectory(directory) ? new(this, directory) : null;
    }

    private bool CheckScopeDirectory(string directory)
    {
        // The configured root's parent is trusted. Reject link and junction
        // components from the root through the scope and entry paths.
        if (!CheckDirectory(_root))
            return false;
        var parent = Path.GetDirectoryName(directory)!;
        return (parent == _root || CheckDirectory(parent)) && CheckDirectory(directory);
    }

    internal static string EntryName(string key)
    {
        if (OperatingSystem.IsWindows() && IsWindowsDeviceName(key))
            throw new MemoryValidationException("The memory key is reserved by the filesystem.");
        return key + ".md";
    }

    private static bool IsEntryFile(string path, bool rejectUnsafe)
    {
        if (new FileInfo(path).LinkTarget is not null)
            return RejectUnsafeEntry(rejectUnsafe);
        var attributes = GetAttributes(path);
        if (attributes is null)
            return false;
        return (attributes.Value & UnsafeFileAttributes) == 0 || RejectUnsafeEntry(rejectUnsafe);
    }

    private static bool RejectUnsafeEntry(bool rejectUnsafe) => rejectUnsafe
        ? throw new MemoryValidationException("A memory entry must be a regular file within its scope.")
        : false;

    private async Task<RawFile?> ReadFileAsync(string directory, string path, bool rejectUnsafe, CancellationToken ct)
    {
        if (!CheckScopeDirectory(directory) || !IsEntryFile(path, rejectUnsafe))
            return null;

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if ((File.GetAttributes(stream.SafeFileHandle) & UnsafeFileAttributes) != 0 || !stream.CanSeek)
            {
                RejectUnsafeEntry(rejectUnsafe);
                return null;
            }
            if (!CheckScopeDirectory(directory) || !IsEntryFile(path, rejectUnsafe))
                return null;

            return await ReadStreamAsync(stream, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task<RawFile> ReadStreamAsync(FileStream stream, CancellationToken ct)
    {
        if (stream.Length > MaxReadBytes)
            throw new IOException($"A memory entry must be at most {MaxReadBytes} bytes.");

        var updatedAtUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        using var contents = new MemoryStream();
        var buffer = new byte[8192];
        var remaining = MaxReadBytes;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), ct);
            if (read == 0)
                return new RawFile(contents.ToArray(), updatedAtUtc);
            if (read > remaining)
                throw new IOException($"A memory entry must be at most {MaxReadBytes} bytes.");

            contents.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    internal IEnumerable<string> EnumerateAreaDirectories()
    {
        var areas = ChildPath(_root, "areas");
        if (!CheckDirectory(_root) || !CheckDirectory(areas))
            return [];

        return new DirectoryInfo(areas).EnumerateDirectories()
            .Where(entry => entry.LinkTarget is null && (entry.Attributes & UnsafeDirectoryAttributes) == 0)
            .Select(entry => entry.FullName);
    }

    internal void CleanupStaleTemporaryFiles(string directory, DateTime cutoffUtc, CancellationToken ct)
    {
        if (!CheckScopeDirectory(directory))
            return;

        foreach (var path in Directory.EnumerateFiles(directory, ".*.tmp"))
        {
            ct.ThrowIfCancellationRequested();
            if (IsEntryFile(path, rejectUnsafe: false) && File.GetLastWriteTimeUtc(path) < cutoffUtc)
                File.Delete(path);
        }
    }

    internal void DeleteAreaDirectory(string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (CheckScopeDirectory(directory))
            Directory.Delete(directory, recursive: true);
    }

    private async Task<DateTime> WriteFileAsync(string directory, string target, byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireScopeDirectory(directory);
        var temporary = ChildPath(directory, $".{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var temporaryCreated = false;
        try
        {
            DateTime updatedAtUtc;
            await using (var stream = new FileStream(temporary, options))
            {
                temporaryCreated = true;
                await stream.WriteAsync(bytes, ct);
                stream.Flush(flushToDisk: true);
                updatedAtUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            }

            ct.ThrowIfCancellationRequested();
            RequireScopeDirectory(directory);
            IsEntryFile(target, rejectUnsafe: true);
            File.Move(temporary, target, overwrite: true);
            temporaryCreated = false;
            return updatedAtUtc;
        }
        finally
        {
            if (temporaryCreated)
                TryCleanup(() =>
                {
                    if (CheckScopeDirectory(directory))
                        File.Delete(temporary);
                });
        }
    }

    private void RequireScopeDirectory(string directory)
    {
        if (!CheckScopeDirectory(directory))
            throw new DirectoryNotFoundException(directory);
    }

    internal static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MemoryValidationException)
        {
            // The scope may already be gone or have been replaced by an unsafe path.
        }
    }

    private string AreaPath(string? areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId) || areaId.AsSpan().Trim().Length != areaId.Length
            || areaId.Any(c => char.IsControl(c) || c is '.' or '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            || IsWindowsDeviceName(areaId))
        {
            throw new MemoryValidationException("An Area ID must be a safe single path segment without dots or control characters.");
        }

        return ChildPath(ChildPath(_root, "areas"), areaId);
    }

    private static bool IsWindowsDeviceName(string value)
    {
        var upper = value.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4 && upper[..3] is "COM" or "LPT" && "123456789¹²³".Contains(upper[3]));
    }

    private static string ChildPath(string parent, string name)
    {
        var path = Path.GetFullPath(Path.Combine(parent, name));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(path), parent, comparison))
            throw new MemoryValidationException("The memory path must remain inside its scope.");
        return path;
    }

    private static bool CheckDirectory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new MemoryValidationException("The memory directory must be a real directory within its scope.");
        var attributes = GetAttributes(path);
        if (attributes is null)
            return false;
        if ((attributes.Value & UnsafeDirectoryAttributes) != 0 || (attributes.Value & FileAttributes.Directory) == 0)
            throw new MemoryValidationException("The memory directory must be a real directory within its scope.");
        return true;
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal sealed record RawFile(byte[] Bytes, DateTime UpdatedAtUtc);

    internal sealed class ScopeDirectory(MemoryDirectory owner, string directory)
    {
        internal IEnumerable<string> EnumerateFileNames()
        {
            owner.RequireScopeDirectory(directory);
            return new DirectoryInfo(directory).EnumerateFileSystemInfos()
                .Where(entry => entry is not DirectoryInfo || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                .Select(entry => entry.Name);
        }

        internal Task<RawFile?> ReadFileAsync(string name, bool rejectUnsafe, CancellationToken ct) =>
            owner.ReadFileAsync(directory, ChildPath(directory, name), rejectUnsafe, ct);

        internal Task<DateTime> WriteFileAsync(string name, byte[] bytes, CancellationToken ct) =>
            owner.WriteFileAsync(directory, ChildPath(directory, name), bytes, ct);

        internal bool Delete(string name)
        {
            owner.RequireScopeDirectory(directory);
            var path = ChildPath(directory, name);
            if (!IsEntryFile(path, rejectUnsafe: true))
                return false;
            File.Delete(path);
            return true;
        }
    }
}
