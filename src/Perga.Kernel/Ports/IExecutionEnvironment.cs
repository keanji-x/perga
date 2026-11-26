using System.Text;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Kernel.Ports;

public interface IExecutionFileSystem
{
    Task<HostListDirResultDto> ListDirectoryAsync(string? path, CancellationToken ct = default);
    Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default);
    Task<string> ResolveCanonicalPathAsync(string path, CancellationToken ct = default);
    Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(
        string workspaceRoot,
        string? relativePath,
        int maxBytes,
        CancellationToken ct = default);
    Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(
        string workspaceRoot,
        string? relativePath,
        CancellationToken ct = default);
    Task<string> ValidateWorkspaceFileAsync(string workspaceRoot, string path, CancellationToken ct = default);
    Task<WorkspaceFileSearchResultDto> SearchWorkspaceFilesAsync(
        string workspaceRoot, string? query, int limit, CancellationToken ct = default);
}

public interface IExecutionEnvironment : IAsyncDisposable
{
    ExecutionTargetDto Descriptor { get; }
    string WorkspacesRoot { get; }
    bool IsLocal { get; }

    IAgentRuntime AgentRuntime { get; }
    IShellCommandRunner Shell { get; }
    IVcsService Vcs { get; }
    IGitHubForgeService? Forge { get; }
    IWorkspaceDriverRegistry WorkspaceDrivers { get; }
    IExecutionFileSystem Files { get; }
    ITerminalBridgeClient Terminals { get; }

    Task<string> ResolveMcpBaseUriAsync(string hostMcpBaseUri, CancellationToken ct = default);
}

public interface IExecutionEnvironmentRegistry : IAsyncDisposable
{
    IExecutionEnvironment Default { get; }
    IExecutionEnvironment GetRequired(string targetId);
    IExecutionEnvironment? TryGet(string targetId);
    IReadOnlyList<ExecutionTargetDto> ListTargets();
    void Register(IExecutionEnvironment environment) => throw new NotSupportedException("Dynamic execution targets are not supported by this registry.");
    ValueTask<bool> UnregisterAsync(string targetId) => ValueTask.FromResult(false);
}

public interface IExecutionTargetManager
{
    Task LoadPersistedTargetsAsync(CancellationToken ct = default);
    Task<ExecutionTargetDto> CreateSshTargetAsync(CreateExecutionTargetRequest request, CancellationToken ct = default);
    Task<bool> DeleteTargetAsync(string targetId, CancellationToken ct = default);
}

public static class ExecutionTargetIds
{
    public const string Local = "local";

    public static bool IsLocal(string? targetId) => Normalize(targetId) == Local;

    public static string Normalize(string? targetId) =>
        string.IsNullOrWhiteSpace(targetId) ? Local : targetId.Trim();
}

public sealed partial class LocalExecutionFileSystem : IExecutionFileSystem
{
    private const int BinarySniffBytes = 8 * 1024;

    public static LocalExecutionFileSystem Instance { get; } = new();

    public Task<HostListDirResultDto> ListDirectoryAsync(string? path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ListDirectory(path));
    }

    public HostListDirResultDto ListDirectory(string? path)
    {
        var rawTarget = string.IsNullOrWhiteSpace(path) ? HomeDirectory() : ExpandHome(path.Trim());

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawTarget);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw KernelException.InvalidArgument($"Invalid directory path '{rawTarget}': {ex.Message}");
        }

        if (File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw KernelException.InvalidArgument($"Path '{fullPath}' is a file, not a directory.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw KernelException.NotFound($"Directory '{fullPath}' not found.");
        }

        try
        {
            using var openCheck = Directory
                .EnumerateFileSystemEntries(fullPath, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false
                })
                .GetEnumerator();
            _ = openCheck;
        }
        catch (UnauthorizedAccessException)
        {
            throw KernelException.Forbidden($"Access denied reading directory '{fullPath}'.");
        }

        var entries = new List<HostDirEntryDto>();
        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };
        foreach (var childFullPath in Directory.EnumerateFileSystemEntries(fullPath, "*", enumOptions))
        {
            try
            {
                var name = Path.GetFileName(childFullPath);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var attrs = File.GetAttributes(childFullPath);
                var isSymlink = (attrs & FileAttributes.ReparsePoint) != 0;
                bool isDir;
                if (isSymlink)
                {
                    try
                    {
                        isDir = Directory.Exists(childFullPath);
                    }
                    catch
                    {
                        isDir = (attrs & FileAttributes.Directory) != 0;
                    }
                }
                else
                {
                    isDir = (attrs & FileAttributes.Directory) != 0;
                }

                entries.Add(new HostDirEntryDto(name, isDir, isSymlink));
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible child entries without failing parent listing.
            }
            catch (IOException)
            {
                // Skip transiently vanishing or special child entries.
            }
        }

        entries.Sort(static (a, b) =>
        {
            if (a.IsDir != b.IsDir)
            {
                return a.IsDir ? -1 : 1;
            }

            var cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });

        var parent = Directory.GetParent(fullPath)?.FullName;
        return new HostListDirResultDto(fullPath, parent, entries);
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Directory.Exists(path));
    }

    public Task<string> ResolveCanonicalPathAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WorkspaceFileReader.ResolveCanonicalPath(path));
    }

    public async Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(
        string workspaceRoot,
        string? relativePath,
        int maxBytes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (root, canonicalPath) = ResolveWorkspacePath(workspaceRoot, relativePath);
        var relFile = DisplayWorkspacePath(root, canonicalPath);
        var kind = UseWorkspaceFileReader(() => WorkspaceFileReader.GetEntryKind(canonicalPath));
        if (kind == WorkspaceFileReader.EntryKind.Directory)
            return UseWorkspaceFileReader(() => BrowseDirectory(root, canonicalPath, relFile, ct));
        RequireRegularFile(kind);

        await using var fs = UseWorkspaceFileReader(() => WorkspaceFileReader.OpenRegularFile(canonicalPath));
        var limit = Math.Max(1, maxBytes);
        using var preview = new MemoryStream();
        var buffer = new byte[Math.Min(limit, BinarySniffBytes)];
        while (preview.Length < limit)
        {
            var bytesRead = await fs.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)preview.Length)), ct).ConfigureAwait(false);
            if (bytesRead == 0) break;
            preview.Write(buffer, 0, bytesRead);
        }
        var read = (int)preview.Length;
        var bytes = preview.GetBuffer();
        var truncated = read == limit && await fs.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0;

        var sniffLen = Math.Min(read, BinarySniffBytes);
        if (bytes.AsSpan(0, sniffLen).IndexOf((byte)0) >= 0)
        {
            return new WorkspaceFileResultDto(
                relFile,
                IsDirectory: false,
                Content: null,
                Truncated: false,
                Entries: null,
                IsBinary: true);
        }

        var validLen = truncated ? TrimTrailingIncompleteUtf8(bytes.AsSpan(0, read)) : read;
        var text = Encoding.UTF8.GetString(bytes, 0, validLen);

        return new WorkspaceFileResultDto(
            relFile,
            IsDirectory: false,
            Content: text,
            Truncated: truncated,
            Entries: null,
            IsBinary: false);
    }

    private static WorkspaceFileResultDto BrowseDirectory(
        string root, string canonicalPath, string relativePath, CancellationToken ct)
    {
        var entries = new List<WorkspaceFileEntryDto>();
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var child in Directory.EnumerateFileSystemEntries(canonicalPath, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var kind = WorkspaceFileReader.GetEntryKind(child);
                if (kind == WorkspaceFileReader.EntryKind.Other) continue;
                var name = Path.GetFileName(child);
                var path = WorkspaceFileReader.ResolveExistingPath(child);
                var directory = kind == WorkspaceFileReader.EntryKind.Directory;
                entries.Add(new WorkspaceFileEntryDto(name, DisplayWorkspacePath(root, path),
                    IsDirectory: directory, SizeBytes: directory ? 0 : new FileInfo(child).Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Entries can disappear, be inaccessible, or be broken links.
            }
        }

        return new WorkspaceFileResultDto(relativePath, IsDirectory: true, Content: null,
            Truncated: false, Entries: entries.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray(), IsBinary: false);
    }

    public Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(
        string workspaceRoot, string? relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (root, canonicalPath) = ResolveWorkspacePath(workspaceRoot, relativePath);
        var kind = UseWorkspaceFileReader(() => WorkspaceFileReader.GetEntryKind(canonicalPath));
        if (kind == WorkspaceFileReader.EntryKind.Directory)
            throw KernelException.NotFound($"Path '{canonicalPath}' is a directory, not a file.");
        RequireRegularFile(kind);

        return Task.FromResult<(string Path, Stream Content)>(
            (DisplayWorkspacePath(root, canonicalPath), UseWorkspaceFileReader(() => WorkspaceFileReader.OpenRegularFile(canonicalPath))));
    }

    public Task<string> ValidateWorkspaceFileAsync(string workspaceRoot, string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (root, canonicalPath) = ResolveWorkspacePath(workspaceRoot, path);
        RequireRegularFile(UseWorkspaceFileReader(() => WorkspaceFileReader.GetEntryKind(canonicalPath)));
        using var stream = UseWorkspaceFileReader(() => WorkspaceFileReader.OpenRegularFile(canonicalPath));
        return Task.FromResult(DisplayWorkspacePath(root, canonicalPath));
    }

    private static void RequireRegularFile(WorkspaceFileReader.EntryKind kind)
    {
        if (kind != WorkspaceFileReader.EntryKind.RegularFile)
            throw KernelException.InvalidArgument("Path is not a regular file.");
    }

    private static (string Root, string Candidate) ResolveWorkspacePath(string workspaceRoot, string? path)
    {
        if (path?.Contains('\0') == true)
            throw KernelException.InvalidArgument("Path cannot contain a null character.");

        return UseWorkspaceFileReader(() =>
        {
            var root = Path.TrimEndingDirectorySeparator(
                WorkspaceFileReader.ResolveExistingPath(Path.GetFullPath(workspaceRoot)));
            var target = string.IsNullOrEmpty(path) ? root : ExpandHome(path);
            // On Unix, realpath resolves a symlink before processing the following '..'.
            var candidate = OperatingSystem.IsWindows() ? Path.GetFullPath(target, root) : Path.Combine(root, target);
            return (root, WorkspaceFileReader.ResolveExistingPath(candidate));
        });
    }

    private static string DisplayWorkspacePath(string root, string canonicalPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(root, canonicalPath, comparison)) return string.Empty;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return canonicalPath.StartsWith(prefix, comparison)
            ? EscapeWorkspaceRelativePath(ToWirePath(canonicalPath[prefix.Length..]))
            : ToWirePath(canonicalPath);
    }

    private static string ToWirePath(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    // A relative path starting with a literal tilde component must survive the next read.
    private static string EscapeWorkspaceRelativePath(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal) ? "./" + path : path;

    private static string HomeDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(path)
            ? Environment.GetEnvironmentVariable("HOME") ?? Path.DirectorySeparatorChar.ToString()
            : path;
    }

    private static string ExpandHome(string path)
    {
        if (path == "~") return HomeDirectory();
        if (path.StartsWith("~/", StringComparison.Ordinal) ||
            (OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal)))
            return HomeDirectory() + Path.DirectorySeparatorChar + path[2..];
        return path;
    }

    public static (string RootFull, string CandidateFull, string SanitizedRel) ResolveContainedWorkspacePathOrThrow(
        string workspaceRoot,
        string? rawPath)
    {
        var rootFull = Path.GetFullPath(workspaceRoot);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return (rootFull, rootFull, string.Empty);
        }

        var trimmed = rawPath.Trim();
        string candidateFull;
        if (Path.IsPathRooted(trimmed))
        {
            var rootedFull = Path.GetFullPath(trimmed);
            if (string.Equals(rootedFull, rootFull, StringComparison.Ordinal) ||
                rootedFull.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                candidateFull = rootedFull;
            }
            else
            {
                candidateFull = Path.GetFullPath(Path.Combine(rootFull, trimmed.TrimStart('/', '\\')));
            }
        }
        else
        {
            candidateFull = Path.GetFullPath(Path.Combine(rootFull, trimmed));
        }

        if (!string.Equals(candidateFull, rootFull, StringComparison.Ordinal) &&
            !candidateFull.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw KernelException.Forbidden("Path escapes workspace root.");
        }

        var sanitizedRel = string.Equals(candidateFull, rootFull, StringComparison.Ordinal)
            ? string.Empty
            : Path.GetRelativePath(rootFull, candidateFull).Replace('\\', '/');
        return (rootFull, candidateFull, sanitizedRel);
    }

    public static int TrimTrailingIncompleteUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0;
        }

        var i = bytes.Length - 1;
        var cont = 0;
        while (i >= 0 && cont < 4 && (bytes[i] & 0b1100_0000) == 0b1000_0000)
        {
            i--;
            cont++;
        }

        if (i < 0)
        {
            return bytes.Length;
        }

        var lead = bytes[i];
        var expected = lead switch
        {
            < 0b1000_0000 => 1,
            >= 0b1111_0000 => 4,
            >= 0b1110_0000 => 3,
            >= 0b1100_0000 => 2,
            _ => 1
        };

        return (bytes.Length - i) < expected ? i : bytes.Length;
    }

    private static T UseWorkspaceFileReader<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw KernelException.NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw KernelException.Forbidden(ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw KernelException.InvalidArgument(ex.Message);
        }
    }
}

public sealed class LocalExecutionEnvironment : IExecutionEnvironment
{
    public LocalExecutionEnvironment(
        string workspacesRoot,
        IAgentRuntime agentRuntime,
        IShellCommandRunner shell,
        IVcsService vcs,
        IWorkspaceDriverRegistry workspaceDrivers,
        ITerminalBridgeClient terminals,
        IGitHubForgeService? forge = null,
        IExecutionFileSystem? files = null,
        string? label = null)
    {
        WorkspacesRoot = workspacesRoot ?? throw new ArgumentNullException(nameof(workspacesRoot));
        AgentRuntime = agentRuntime ?? throw new ArgumentNullException(nameof(agentRuntime));
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Vcs = vcs ?? throw new ArgumentNullException(nameof(vcs));
        WorkspaceDrivers = workspaceDrivers ?? throw new ArgumentNullException(nameof(workspaceDrivers));
        Terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        Forge = forge;
        Files = files ?? LocalExecutionFileSystem.Instance;
        var resolvedLabel = string.IsNullOrWhiteSpace(label)
            ? $"Local ({Environment.UserName})"
            : label.Trim();
        Descriptor = new ExecutionTargetDto(ExecutionTargetIds.Local, resolvedLabel, "local", IsDefault: true);
    }

    public ExecutionTargetDto Descriptor { get; }
    public string WorkspacesRoot { get; }
    public bool IsLocal => true;
    public IAgentRuntime AgentRuntime { get; }
    public IShellCommandRunner Shell { get; }
    public IVcsService Vcs { get; }
    public IGitHubForgeService? Forge { get; }
    public IWorkspaceDriverRegistry WorkspaceDrivers { get; }
    public IExecutionFileSystem Files { get; }
    public ITerminalBridgeClient Terminals { get; }

    public Task<string> ResolveMcpBaseUriAsync(string hostMcpBaseUri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(hostMcpBaseUri);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ExecutionEnvironmentRegistry : IExecutionEnvironmentRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IExecutionEnvironment> _environments;
    private readonly List<string> _order;

    public ExecutionEnvironmentRegistry(IEnumerable<IExecutionEnvironment> environments)
    {
        ArgumentNullException.ThrowIfNull(environments);
        _environments = new Dictionary<string, IExecutionEnvironment>(StringComparer.Ordinal);
        _order = [];
        foreach (var env in environments)
        {
            ArgumentNullException.ThrowIfNull(env);
            if (!_environments.TryAdd(env.Descriptor.Id, env))
            {
                throw new ArgumentException($"Duplicate execution target id '{env.Descriptor.Id}'.", nameof(environments));
            }

            _order.Add(env.Descriptor.Id);
        }

        if (!_environments.TryGetValue(ExecutionTargetIds.Local, out var localEnv))
        {
            throw new ArgumentException($"Execution environment registry must include '{ExecutionTargetIds.Local}'.", nameof(environments));
        }

        Default = localEnv;
    }

    public IExecutionEnvironment Default { get; }

    public IExecutionEnvironment GetRequired(string targetId)
    {
        var normalized = ExecutionTargetIds.Normalize(targetId);
        lock (_gate)
        {
            if (_environments.TryGetValue(normalized, out var env))
            {
                return env;
            }
        }

        throw KernelException.PreconditionFailed(
            $"Execution target '{normalized}' is not configured on this Host.");
    }

    public IExecutionEnvironment? TryGet(string targetId)
    {
        var normalized = ExecutionTargetIds.Normalize(targetId);
        lock (_gate)
        {
            return _environments.TryGetValue(normalized, out var env) ? env : null;
        }
    }

    public IReadOnlyList<ExecutionTargetDto> ListTargets()
    {
        lock (_gate)
        {
            return _order
                .Where(_environments.ContainsKey)
                .Select(id => _environments[id].Descriptor)
                .ToArray();
        }
    }

    public void Register(IExecutionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var id = ExecutionTargetIds.Normalize(environment.Descriptor.Id);
        if (string.Equals(id, ExecutionTargetIds.Local, StringComparison.Ordinal))
        {
            throw new ArgumentException("Execution target id 'local' cannot be replaced.", nameof(environment));
        }

        lock (_gate)
        {
            if (!_environments.ContainsKey(id))
            {
                _order.Add(id);
            }

            _environments[id] = environment;
        }
    }

    public async ValueTask<bool> UnregisterAsync(string targetId)
    {
        var id = ExecutionTargetIds.Normalize(targetId);
        if (string.Equals(id, ExecutionTargetIds.Local, StringComparison.Ordinal))
        {
            return false;
        }

        IExecutionEnvironment? removed;
        lock (_gate)
        {
            if (!_environments.Remove(id, out removed))
            {
                return false;
            }

            _order.Remove(id);
        }

        await removed.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        IExecutionEnvironment[] snapshot;
        lock (_gate)
        {
            snapshot = _environments.Values.ToArray();
        }

        foreach (var env in snapshot)
        {
            if (!ReferenceEquals(env, Default))
            {
                await env.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
