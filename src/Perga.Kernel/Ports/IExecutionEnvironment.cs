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
        string relativePath,
        CancellationToken ct = default);
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

    public static string Normalize(string? targetId) =>
        string.IsNullOrWhiteSpace(targetId) ? Local : targetId.Trim();
}

public sealed class LocalExecutionFileSystem : IExecutionFileSystem
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
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homeDir))
        {
            homeDir = Environment.GetEnvironmentVariable("HOME") ?? Path.DirectorySeparatorChar.ToString();
        }

        string rawTarget;
        if (string.IsNullOrWhiteSpace(path))
        {
            rawTarget = homeDir;
        }
        else
        {
            var trimmed = path.Trim();
            if (string.Equals(trimmed, "~", StringComparison.Ordinal))
            {
                rawTarget = homeDir;
            }
            else if (trimmed.StartsWith("~/", StringComparison.Ordinal) ||
                     trimmed.StartsWith("~\\", StringComparison.Ordinal))
            {
                rawTarget = Path.Combine(homeDir, trimmed[2..]);
            }
            else
            {
                rawTarget = trimmed;
            }
        }

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
        var (rootFull, candidateFull, sanitizedRel) = ResolveContainedWorkspacePathOrThrow(workspaceRoot, relativePath);

        if (Directory.Exists(candidateFull))
        {
            var containedDirectory = EnsureRealPathContainedInWorkspace(rootFull, candidateFull);
            using var openedDirectory = UseWorkspaceFileReader(() => WorkspaceFileReader.OpenDirectory(containedDirectory));
            var directoryPath = openedDirectory is null ? containedDirectory
                : $"/proc/self/fd/{openedDirectory.DangerousGetHandle()}";
            var entries = new List<WorkspaceFileEntryDto>();
            foreach (var dir in Directory.GetDirectories(directoryPath).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                var rel = Path.GetRelativePath(rootFull, Path.Combine(candidateFull, name)).Replace('\\', '/');
                entries.Add(new WorkspaceFileEntryDto(name, rel, IsDirectory: true, SizeBytes: 0));
            }

            foreach (var file in Directory.GetFiles(directoryPath).OrderBy(f => f, StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                var rel = Path.GetRelativePath(rootFull, Path.Combine(candidateFull, info.Name)).Replace('\\', '/');
                entries.Add(new WorkspaceFileEntryDto(info.Name, rel, IsDirectory: false, SizeBytes: info.Length));
            }

            var relDir = string.Equals(candidateFull, rootFull, StringComparison.Ordinal)
                ? string.Empty
                : Path.GetRelativePath(rootFull, candidateFull).Replace('\\', '/');
            return new WorkspaceFileResultDto(
                relDir,
                IsDirectory: true,
                Content: null,
                Truncated: false,
                Entries: entries,
                IsBinary: false);
        }

        if (!File.Exists(candidateFull))
        {
            throw KernelException.NotFound($"Workspace path '{sanitizedRel}' not found.");
        }

        var canonicalPath = EnsureRealPathContainedInWorkspace(rootFull, candidateFull);
        await using var fs = UseWorkspaceFileReader(() => WorkspaceFileReader.Open(canonicalPath));
        var limit = Math.Max(1, maxBytes);
        var buffer = new byte[Math.Min(fs.Length, limit)];
        var read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        var relFile = Path.GetRelativePath(rootFull, candidateFull).Replace('\\', '/');

        var sniffLen = Math.Min(read, BinarySniffBytes);
        if (buffer.AsSpan(0, sniffLen).IndexOf((byte)0) >= 0)
        {
            return new WorkspaceFileResultDto(
                relFile,
                IsDirectory: false,
                Content: null,
                Truncated: false,
                Entries: null,
                IsBinary: true);
        }

        var truncated = fs.Length > read;
        var validLen = truncated ? TrimTrailingIncompleteUtf8(buffer.AsSpan(0, read)) : read;
        var text = Encoding.UTF8.GetString(buffer, 0, validLen);

        return new WorkspaceFileResultDto(
            relFile,
            IsDirectory: false,
            Content: text,
            Truncated: truncated,
            Entries: null,
            IsBinary: false);
    }

    public Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(
        string workspaceRoot,
        string relativePath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (rootFull, candidateFull, sanitizedRel) = ResolveContainedWorkspacePathOrThrow(workspaceRoot, relativePath);

        if (Directory.Exists(candidateFull))
        {
            throw KernelException.NotFound($"Workspace path '{sanitizedRel}' is a directory, not a file.");
        }

        if (!File.Exists(candidateFull))
        {
            throw KernelException.NotFound($"Workspace file '{sanitizedRel}' not found.");
        }

        var canonicalPath = EnsureRealPathContainedInWorkspace(rootFull, candidateFull);
        return Task.FromResult<(string Path, Stream Content)>(
            (canonicalPath, UseWorkspaceFileReader(() => WorkspaceFileReader.Open(canonicalPath))));
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

    private static string EnsureRealPathContainedInWorkspace(string rootFull, string candidateFull) =>
        UseWorkspaceFileReader(() => WorkspaceFileReader.ResolveContainedPath(rootFull, candidateFull));

    private static T UseWorkspaceFileReader<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (FileNotFoundException ex)
        {
            throw KernelException.NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw KernelException.Forbidden(ex.Message);
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
