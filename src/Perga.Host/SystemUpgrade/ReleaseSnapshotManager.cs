using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Perga.Host.Configuration;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Templates;
using Perga.PolicyVm;

namespace Perga.Host.SystemUpgrade;

/// <summary>Publishes one validated web/template identity. Retains old asset roots for existing tabs.</summary>
public sealed class ReleaseSnapshotManager : ITemplateCatalog, IFileProvider, IDisposable
{
    private sealed record Snapshot(string ReleaseId, string WebBuildId, string HostBuildId, string BridgeBuildId,
        ITemplateCatalog Templates, PhysicalFileProvider? Files, IReadOnlyList<PhysicalFileProvider> OldFiles);
    private readonly PergaHostOptions _options;
    private readonly IPolicyVm _vm;
    private readonly object _swap = new();
    private Snapshot _snapshot;
    public (string ReleaseId, string WebBuildId, string HostBuildId, string BridgeBuildId) Identity
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return (snapshot.ReleaseId, snapshot.WebBuildId, snapshot.HostBuildId, snapshot.BridgeBuildId);
        }
    }
    public string ReleaseId => Volatile.Read(ref _snapshot).ReleaseId;
    public string WebBuildId => Volatile.Read(ref _snapshot).WebBuildId;
    public string HostBuildId => Volatile.Read(ref _snapshot).HostBuildId;
    public string BridgeBuildId => Volatile.Read(ref _snapshot).BridgeBuildId;

    public ReleaseSnapshotManager(PergaHostOptions options, IPolicyVm vm)
    {
        _options = options;
        _vm = vm;
        if (options.InstallationRoot is not null)
        {
            var (directory, manifest) = VerifyCurrent();
            var components = manifest.GetProperty("components");
            _snapshot = Build(directory, manifest, components.GetProperty("host").GetString()!,
                options.BridgeBuildId ?? "unknown", []);
        }
        else
        {
            _snapshot = new(options.Version, options.Version, options.Version, options.BridgeBuildId ?? "unknown",
                PergaHostServiceCollectionExtensions.CreateResilientTemplateCatalog(vm, options.TemplatesDirectory),
                options.StaticWebRootDirectory is { } web && Directory.Exists(web) ? new PhysicalFileProvider(Path.GetFullPath(web)) : null, []);
        }
    }

    public SystemReloadResponse Reload(string releaseId, string webBuildId)
    {
        lock (_swap)
        {
            var (directory, manifest) = VerifyCurrent();
            if (manifest.GetProperty("releaseId").GetString() != releaseId || manifest.GetProperty("webBuildId").GetString() != webBuildId)
                throw KernelException.Conflict("Reload identity does not match the verified current manifest.");
            var old = _snapshot;
            var previous = old.Files is null ? old.OldFiles.ToArray() : old.OldFiles.Prepend(old.Files).ToArray();
            var retained = previous.Take(3).ToArray();
            var next = Build(directory, manifest, old.HostBuildId, old.BridgeBuildId, retained);
            Volatile.Write(ref _snapshot, next);
            foreach (var evicted in previous.Skip(3))
                evicted.Dispose();
            return new(next.ReleaseId, next.WebBuildId);
        }
    }

    private Snapshot Build(string directory, JsonElement manifest, string hostId, string bridgeId, IReadOnlyList<PhysicalFileProvider> old) =>
        new(manifest.GetProperty("releaseId").GetString()!, manifest.GetProperty("webBuildId").GetString()!, hostId, bridgeId,
            PergaHostServiceCollectionExtensions.CreateResilientTemplateCatalog(_vm, Path.Combine(directory, "templates")),
            new PhysicalFileProvider(Path.Combine(directory, "web")), old);

    private (string Directory, JsonElement Manifest) VerifyCurrent()
    {
        if (_options.InstallationRoot is null)
        {
            throw KernelException.Conflict("Managed installation root is not configured.");
        }
        var root = Path.GetFullPath(_options.InstallationRoot);
        var link = new DirectoryInfo(Path.Combine(root, "current"));
        var directory = link.ResolveLinkTarget(true)?.FullName ?? throw KernelException.InvalidArgument("current must be a release symlink.");
        var releases = Path.Combine(root, "releases") + Path.DirectorySeparatorChar;
        if (Path.GetDirectoryName(directory) + Path.DirectorySeparatorChar != releases || !Directory.Exists(directory))
            throw KernelException.InvalidArgument("current is outside the installation releases directory.");
        var manifestPath = Path.Combine(directory, "manifest.json");
        if ((File.GetAttributes(manifestPath) & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw KernelException.InvalidArgument("Manifest must be a regular file.");
        JsonElement manifest;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            manifest = document.RootElement.Clone();
            if (manifest.ValueKind != JsonValueKind.Object ||
                !manifest.TryGetProperty("formatVersion", out var format) || !format.TryGetInt32(out var formatVersion) || formatVersion != 1 ||
                !manifest.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var schemaVersion) || schemaVersion < 0 ||
                !manifest.TryGetProperty("releaseId", out var release) || release.ValueKind != JsonValueKind.String ||
                release.GetString() is not { } id || !Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") ||
                Path.GetFileName(directory) != id ||
                !manifest.TryGetProperty("webBuildId", out var web) || web.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(web.GetString()) ||
                !manifest.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Object ||
                components.EnumerateObject().Count() != 4)
                throw KernelException.InvalidArgument("Invalid release manifest.");
            foreach (var name in new[] { "host", "web", "templates", "termBridge" })
            {
                if (!components.TryGetProperty(name, out var hash) || hash.ValueKind != JsonValueKind.String ||
                    !Regex.IsMatch(hash.GetString()!, @"\A[0-9a-f]{64}\z"))
                    throw KernelException.InvalidArgument("Invalid manifest components.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw KernelException.InvalidArgument("Invalid release manifest.");
        }
        // perga-ctl verifies payload integrity before publishing current. The Host
        // checks only the roots it consumes, without reading resident binaries.
        foreach (var name in new[] { "web", "templates" })
        {
            var path = Path.Combine(directory, name);
            if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw KernelException.InvalidArgument("Missing component or forbidden component link.");
        }
        return (directory, manifest);
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var current = snapshot.Files?.GetFileInfo(subpath);
        if (current?.Exists == true)
        {
            return current;
        }
        if (!subpath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            foreach (var old in snapshot.OldFiles)
                if (old.GetFileInfo(subpath) is { Exists: true } asset)
                {
                    return asset;
                }
        return new NotFoundFileInfo(subpath);
    }
    public IDirectoryContents GetDirectoryContents(string subpath) => Volatile.Read(ref _snapshot).Files?.GetDirectoryContents(subpath) ?? NotFoundDirectoryContents.Singleton;
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    public IReadOnlyList<CompiledTemplateManifest> ListManifests() => Volatile.Read(ref _snapshot).Templates.ListManifests();
    public IReadOnlyList<CompiledPolicy> ListPolicies() => Volatile.Read(ref _snapshot).Templates.ListPolicies();
    public CompiledPolicy? TryGetPolicy(string id) => Volatile.Read(ref _snapshot).Templates.TryGetPolicy(id);
    public CompiledPolicy GetRequiredPolicy(string id) => Volatile.Read(ref _snapshot).Templates.GetRequiredPolicy(id);
    public CompiledPolicy GetOrCompileSnapshot(string source) => Volatile.Read(ref _snapshot).Templates.GetOrCompileSnapshot(source);
    public CompiledPolicy CompileSource(string source) => Volatile.Read(ref _snapshot).Templates.CompileSource(source);
    public CompiledPolicy RegisterTemplate(string source) => Volatile.Read(ref _snapshot).Templates.RegisterTemplate(source);
    public int LoadFromDirectory(string path) => Volatile.Read(ref _snapshot).Templates.LoadFromDirectory(path);
    public void Dispose()
    {
        var snapshot = _snapshot;
        snapshot.Files?.Dispose();
        foreach (var old in snapshot.OldFiles.Distinct()) old.Dispose();
    }
}
