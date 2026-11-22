using System.Security.Cryptography;
using System.Text;
using Perga.Contracts;

namespace Perga.Kernel.Flow;

/// <summary>
/// Manages durable `{DataDir}/tracks/{trackId}/flow.json` files and `{DataDir}/schemas/flow.v1.schema.json`.
/// Enforces draft protection by never overwriting dirty on-disk files during background GC or cancel writes.
/// </summary>
public sealed class TrackFlowFileStore
{
    private readonly string _dataDir;
    private static readonly Lazy<string> BundledSchemaJson = new(LoadEmbeddedSchemaJson);

    public TrackFlowFileStore(KernelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataDir = !string.IsNullOrWhiteSpace(options.DataDir)
            ? Path.GetFullPath(options.DataDir)
            : Path.GetFullPath(Path.Combine(options.WorkspacesRootDirectory, ".perga-data"));
    }

    public TrackFlowFileStore(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        _dataDir = Path.GetFullPath(dataDir);
    }

    public string DataDir => _dataDir;

    public string GetSchemaFilePath() => Path.Combine(_dataDir, "schemas", "flow.v1.schema.json");

    public string GetFlowFilePath(string trackId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        if (trackId.Contains('/') || trackId.Contains('\\') || trackId.Contains("..", StringComparison.Ordinal))
        {
            throw KernelException.InvalidArgument($"Invalid track id '{trackId}'.");
        }

        var tracksRoot = Path.GetFullPath(Path.Combine(_dataDir, "tracks"));
        var fullPath = Path.GetFullPath(Path.Combine(tracksRoot, trackId, "flow.json"));
        var prefix = tracksRoot.EndsWith(Path.DirectorySeparatorChar)
            ? tracksRoot
            : tracksRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw KernelException.InvalidArgument($"Track flow path for '{trackId}' escapes the data root.");
        }

        return fullPath;
    }

    private void AssertContainedNoSymlinks(string flowPath)
    {
        var tracksRoot = Path.GetFullPath(Path.Combine(_dataDir, "tracks"));
        var trackDir = Path.GetDirectoryName(flowPath)!;
        RejectSymlinkPath(tracksRoot, isDirectory: true);
        RejectSymlinkPath(trackDir, isDirectory: true);
        RejectSymlinkPath(flowPath, isDirectory: false);
    }

    private static void RejectSymlinkPath(string path, bool isDirectory)
    {
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
        {
            return;
        }

        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw KernelException.PolicyDenied($"Refusing to follow symbolic link at '{path}'.");
        }
    }

    public void EnsureSchemaMaterialized()
    {
        var schemaPath = GetSchemaFilePath();
        var dir = Path.GetDirectoryName(schemaPath)!;
        Directory.CreateDirectory(dir);
        var content = BundledSchemaJson.Value;
        if (File.Exists(schemaPath))
        {
            var existing = File.ReadAllText(schemaPath, Encoding.UTF8);
            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return;
            }
        }

        WriteFileAtomic(schemaPath, content);
    }

    public (string FilePath, bool Created, string? WrittenSha256) EnsureFlowFileMaterialized(
        string trackId,
        FlowSpecDto persistedSpec)
    {
        EnsureSchemaMaterialized();
        var flowPath = GetFlowFilePath(trackId);
        if (File.Exists(flowPath))
        {
            return (flowPath, false, null);
        }

        var canonical = TrackFlowSerializer.SerializeCanonical(persistedSpec);
        var (created, sha) = TryWriteFlowFileIfUnmodified(trackId, canonical, null);
        return (flowPath, created, sha);
    }

    public (string Content, string Sha256) ReadFlowFile(string trackId)
    {
        var flowPath = GetFlowFilePath(trackId);
        AssertContainedNoSymlinks(flowPath);
        if (!File.Exists(flowPath))
        {
            throw KernelException.NotFound($"Flow specification file '{flowPath}' does not exist.");
        }

        var bytes = File.ReadAllBytes(flowPath);
        var content = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        return (content, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public (bool Written, string? NewSha256) TryWriteFlowFileIfUnmodified(
        string trackId,
        string canonicalJson,
        string? expectedDiskSha256)
    {
        var path = GetFlowFilePath(trackId);
        AssertContainedNoSymlinks(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        AssertContainedNoSymlinks(path);
        var tmp = Path.Combine(directory, $".flow.json.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, canonicalJson);
            if (File.Exists(path))
            {
                if (expectedDiskSha256 is null ||
                    !string.Equals(ReadFlowFile(trackId).Sha256, expectedDiskSha256, StringComparison.Ordinal))
                {
                    return (false, null);
                }

                File.Move(tmp, path, overwrite: true);
            }
            else
            {
                try { File.Move(tmp, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { return (false, null); }
            }

            return (true, ComputeSha256Hex(canonicalJson));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    public static string ComputeSha256Hex(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static void WriteFileAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Best-effort cleanup of temp file.
            }

            throw;
        }
    }

    private static string LoadEmbeddedSchemaJson()
    {
        using var stream = typeof(TrackFlowFileStore).Assembly.GetManifestResourceStream("Perga.FlowSchemaV1")
            ?? throw new InvalidOperationException("The embedded Track Flow v1 schema is missing.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
