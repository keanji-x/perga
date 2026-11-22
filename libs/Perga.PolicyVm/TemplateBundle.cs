using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Jint;
using Jint.Runtime.Modules;

namespace Perga.PolicyVm;

/// <summary>
/// Represents a single-file template or a multi-file template directory package serialized as a deterministic bundle.
/// Single-file .perga.js sources remain 100% compatible without wrapping.
/// </summary>
public sealed record TemplateBundle(
    string EntryPath,
    IReadOnlyDictionary<string, string> Files,
    bool IsMultiFile,
    IReadOnlyList<TemplateBundle>? SnapshotMultiFileLeaves = null,
    string? RawSnapshotSource = null)
{
    public const string BundleHeader = "// @perga-bundle-v1\n";
    private const int MaxBundleFiles = 64;
    private const int MaxSnapshotDepth = 64;
    private const long MaxSingleFileBytes = 512 * 1024;
    private const long MaxTotalBundleBytes = 2 * 1024 * 1024;
    private const long MaxRawBundleEnvelopeBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".md", ".html", ".htm", ".json", ".css"
    };

    private static readonly HashSet<string> IgnoredTopLevelJsonFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "tsconfig.json", "jsconfig.json", "package.json", "package-lock.json"
    };

    internal const string InheritedSnapshotFormat = "perga.snapshot.v1";

    internal abstract record SnapshotDefinition
    {
        internal sealed record Inherited(TemplateBundle Parent, TemplateBundle Child) : SnapshotDefinition;
    }

    internal SnapshotDefinition? Snapshot { get; private init; }

    public string EntrySource => RawSnapshotSource ?? Files[EntryPath];

    public bool HasMultiFileSources => IsMultiFile || (SnapshotMultiFileLeaves is { Count: > 0 });

    public static TemplateBundle Parse(string source) => ParseInternal(source, depth: 0);

    private static TemplateBundle ParseInternal(string source, int depth)
    {
        if (depth > MaxSnapshotDepth)
        {
            throw new InvalidOperationException("Template inheritance exceeds the maximum depth.");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Template source cannot be empty.", nameof(source));
        }

        if (Encoding.UTF8.GetByteCount(source) > MaxRawBundleEnvelopeBytes)
        {
            throw new InvalidOperationException($"Template source exceeds maximum envelope size ({MaxRawBundleEnvelopeBytes} bytes).");
        }

        if (!source.StartsWith(BundleHeader, StringComparison.Ordinal))
        {
            if (TryParseSnapshot(source, depth, out var snapshotBundle))
            {
                return snapshotBundle;
            }

            return new TemplateBundle(
                EntryPath: "template.perga.js",
                Files: new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["template.perga.js"] = source
                }),
                IsMultiFile: false);
        }

        var json = source[BundleHeader.Length..];
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Template bundle payload must be a JSON object.");
        }

        var entry = root.TryGetProperty("entry", out var entryEl) && entryEl.ValueKind == JsonValueKind.String
            ? NormalizeBundleRelativePath(entryEl.GetString() ?? string.Empty)
            : "template.perga.js";

        if (!root.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Template bundle must declare a 'files' object.");
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var prop in filesEl.EnumerateObject())
        {
            if (files.Count >= MaxBundleFiles)
            {
                throw new InvalidOperationException($"Template bundle exceeds maximum file count ({MaxBundleFiles}).");
            }

            var relPath = NormalizeBundleRelativePath(prop.Name);
            var ext = Path.GetExtension(relPath).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Template bundle file '{relPath}' has unsupported extension '{ext}'.");
            }

            if (files.ContainsKey(relPath))
            {
                throw new InvalidOperationException($"Template bundle contains duplicate normalized path '{relPath}'.");
            }

            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"Template bundle file '{relPath}' must have string contents.");
            }

            var content = prop.Value.GetString() ?? string.Empty;
            var byteCount = Encoding.UTF8.GetByteCount(content);
            if (byteCount > MaxSingleFileBytes)
            {
                throw new InvalidOperationException($"Template bundle file '{relPath}' exceeds maximum size ({MaxSingleFileBytes} bytes).");
            }

            totalBytes += byteCount;
            if (totalBytes > MaxTotalBundleBytes)
            {
                throw new InvalidOperationException($"Template bundle exceeds maximum total size ({MaxTotalBundleBytes} bytes).");
            }

            files[relPath] = content;
        }

        if (!files.ContainsKey(entry))
        {
            throw new InvalidOperationException($"Template bundle entry file '{entry}' was not found in 'files'.");
        }

        return new TemplateBundle(entry, new ReadOnlyDictionary<string, string>(files), IsMultiFile: true);
    }

    private static bool TryParseSnapshot(string source, int depth, out TemplateBundle bundle)
    {
        bundle = null!;
        if (!source.TrimStart().StartsWith('{'))
        {
            return false;
        }

        using var doc = JsonDocument.Parse(source);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("format", out var formatEl) ||
            formatEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var format = formatEl.GetString();
        if (!string.Equals(format, InheritedSnapshotFormat, StringComparison.Ordinal))
        {
            return false;
        }

        if (!root.TryGetProperty("sources", out var sourcesEl) || sourcesEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Invalid template snapshot.");
        }

        var sources = sourcesEl.EnumerateArray().ToArray();
        if (sources.Length != 2 ||
            sources[0].ValueKind != JsonValueKind.String ||
            sources[1].ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Invalid template snapshot.");
        }

        var left = ParseInternal(sources[0].GetString()!, depth + 1);
        var right = ParseInternal(sources[1].GetString()!, depth + 1);

        var leaves = new List<TemplateBundle>();
        AppendMultiFileLeaves(left, leaves);
        AppendMultiFileLeaves(right, leaves);

        // Merge packaged assets (report.md, widgets/*.html, etc.) from base to child so child overrides base.
        var mergedAssets = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeBundleAssets(left, mergedAssets);
        MergeBundleAssets(right, mergedAssets);

        bundle = new TemplateBundle(
            EntryPath: "__snapshot__.json",
            Files: new ReadOnlyDictionary<string, string>(mergedAssets),
            IsMultiFile: false,
            SnapshotMultiFileLeaves: leaves.Count > 0 ? leaves.AsReadOnly() : null,
            RawSnapshotSource: source)
        {
            Snapshot = new SnapshotDefinition.Inherited(left, right),
        };
        return true;
    }

    private static void AppendMultiFileLeaves(TemplateBundle source, List<TemplateBundle> leaves)
    {
        if (source.IsMultiFile)
        {
            leaves.Add(source);
        }
        else if (source.SnapshotMultiFileLeaves is { Count: > 0 } nested)
        {
            leaves.AddRange(nested);
        }
    }

    private static void MergeBundleAssets(TemplateBundle source, Dictionary<string, string> target)
    {
        if (!source.IsMultiFile && source.SnapshotMultiFileLeaves is null)
        {
            return;
        }

        foreach (var kv in source.Files)
        {
            target[kv.Key] = kv.Value;
        }
    }

    public static string PackDirectory(string directoryPath, string entryFileName = "template.perga.js")
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Template package directory '{directoryPath}' was not found.");
        }

        var rootDirInfo = new DirectoryInfo(directoryPath);
        if (IsReparseOrSymlink(rootDirInfo))
        {
            throw new InvalidOperationException($"Template package directory '{directoryPath}' cannot be a symlink.");
        }

        var rootFull = Path.GetFullPath(directoryPath);
        var entryFull = Path.Combine(rootFull, entryFileName);
        if (!File.Exists(entryFull))
        {
            throw new FileNotFoundException($"Template package entry '{entryFileName}' was not found in '{directoryPath}'.", entryFull);
        }

        if (IsReparseOrSymlink(new FileInfo(entryFull)))
        {
            throw new InvalidOperationException($"Template package entry '{entryFileName}' cannot be a symlink.");
        }

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;

        WalkSafeDirectory(rootFull, rootFull, files, ref totalBytes, directoryPath);

        var entryNorm = NormalizeBundleRelativePath(entryFileName);
        if (!files.ContainsKey(entryNorm))
        {
            throw new InvalidOperationException($"Template package entry '{entryNorm}' was not packed.");
        }

        var payload = new
        {
            entry = entryNorm,
            files
        };
        return BundleHeader + JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Single authoritative discovery rule for a templates directory:
    /// top-level `*.perga.js`, `*.perga.json` (and optionally plain `*.json` excluding tooling configs),
    /// plus `&lt;pkg&gt;/template.perga.js` multi-file packages.
    /// Symlinks are skipped to prevent escaping the templates root.
    /// </summary>
    public static IReadOnlyList<string> DiscoverDirectorySources(string directoryPath, bool includePlainJson = true)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return [];
        }

        var sources = new List<string>();
        foreach (var file in Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly)
                     .Where(f =>
                         f.EndsWith(".perga.js", StringComparison.Ordinal) ||
                         f.EndsWith(".perga.json", StringComparison.Ordinal) ||
                         (includePlainJson &&
                          f.EndsWith(".json", StringComparison.Ordinal) &&
                          !IgnoredTopLevelJsonFiles.Contains(Path.GetFileName(f))))
                     .OrderBy(static f => f, StringComparer.Ordinal))
        {
            if (IsReparseOrSymlink(new FileInfo(file)))
            {
                continue;
            }

            sources.Add(File.ReadAllText(file, Encoding.UTF8));
        }

        foreach (var dir in Directory.GetDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static d => d, StringComparer.Ordinal))
        {
            if (IsReparseOrSymlink(new DirectoryInfo(dir)))
            {
                continue;
            }

            var entryPath = Path.Combine(dir, "template.perga.js");
            if (!File.Exists(entryPath) || IsReparseOrSymlink(new FileInfo(entryPath)))
            {
                continue;
            }

            sources.Add(PackDirectory(dir));
        }

        return sources;
    }

    public bool TryGetFile(string relativePath, out string content)
    {
        var normalized = NormalizeBundleRelativePath(relativePath);
        return Files.TryGetValue(normalized, out content!);
    }

    public static string NormalizeBundleRelativePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new InvalidOperationException("Template relative path cannot be empty.");
        }

        var cleaned = rawPath.Trim().Replace('\\', '/');
        while (cleaned.StartsWith("./", StringComparison.Ordinal))
        {
            cleaned = cleaned[2..];
        }

        if (cleaned.StartsWith("/", StringComparison.Ordinal) ||
            cleaned.Contains(':', StringComparison.Ordinal) ||
            cleaned.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Template relative path '{rawPath}' must be relative inside the template package.");
        }

        var parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                throw new InvalidOperationException($"Template relative path '{rawPath}' cannot traverse outside the template package.");
            }

            stack.Add(part);
        }

        if (stack.Count == 0)
        {
            throw new InvalidOperationException($"Template relative path '{rawPath}' is empty after normalization.");
        }

        return string.Join('/', stack);
    }

    public static string ResolveRelative(string? referencingPath, string specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier))
        {
            throw new InvalidOperationException("Import specifier cannot be empty.");
        }

        var spec = specifier.Trim().Replace('\\', '/');
        if (spec.Contains(':', StringComparison.Ordinal) || spec.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Module import '{specifier}' contains invalid characters.");
        }

        if (!spec.StartsWith("./", StringComparison.Ordinal) &&
            !spec.StartsWith("../", StringComparison.Ordinal))
        {
            return NormalizeBundleRelativePath(spec);
        }

        var baseDir = string.Empty;
        if (!string.IsNullOrWhiteSpace(referencingPath))
        {
            var refNorm = NormalizeBundleRelativePath(referencingPath);
            var slash = refNorm.LastIndexOf('/');
            if (slash > 0)
            {
                baseDir = refNorm[..slash];
            }
        }

        var combined = string.IsNullOrEmpty(baseDir) ? spec : $"{baseDir}/{spec}";
        var stack = new List<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException($"Module import '{specifier}' escapes the template package root.");
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(part);
        }

        if (stack.Count == 0)
        {
            throw new InvalidOperationException($"Module import '{specifier}' resolved to an empty path.");
        }

        return string.Join('/', stack);
    }

    private static bool IsReparseOrSymlink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static void WalkSafeDirectory(
        string rootFull,
        string currentDir,
        SortedDictionary<string, string> files,
        ref long totalBytes,
        string displayRoot)
    {
        foreach (var filePath in Directory.EnumerateFiles(currentDir, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static p => p, StringComparer.Ordinal))
        {
            var info = new FileInfo(filePath);
            if (IsReparseOrSymlink(info))
            {
                continue;
            }

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
            {
                continue;
            }

            var rel = Path.GetRelativePath(rootFull, filePath).Replace('\\', '/');
            var normalized = NormalizeBundleRelativePath(rel);
            if (files.Count >= MaxBundleFiles)
            {
                throw new InvalidOperationException($"Template package '{displayRoot}' exceeds maximum file count ({MaxBundleFiles}).");
            }

            if (info.Length > MaxSingleFileBytes)
            {
                throw new InvalidOperationException($"Template package file '{normalized}' exceeds maximum size ({MaxSingleFileBytes} bytes).");
            }

            totalBytes += info.Length;
            if (totalBytes > MaxTotalBundleBytes)
            {
                throw new InvalidOperationException($"Template package '{displayRoot}' exceeds maximum total size ({MaxTotalBundleBytes} bytes).");
            }

            files[normalized] = File.ReadAllText(filePath, Encoding.UTF8);
        }

        foreach (var subDir in Directory.EnumerateDirectories(currentDir, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(static d => d, StringComparer.Ordinal))
        {
            if (IsReparseOrSymlink(new DirectoryInfo(subDir)))
            {
                continue;
            }

            WalkSafeDirectory(rootFull, subDir, files, ref totalBytes, displayRoot);
        }
    }
}

/// <summary>
/// Strict in-memory Jint ES ModuleLoader that resolves relative imports exclusively inside a TemplateBundle
/// (or isolated per-source namespaces inside a frozen `perga.snapshot.v1` inheritance tree).
/// Zero filesystem access; path escapes throw immediately.
/// </summary>
internal sealed class BundleModuleLoader : ModuleLoader
{
    private const string SnapshotPrefix = "__snap_";
    private readonly TemplateBundle _bundle;

    public BundleModuleLoader(TemplateBundle bundle)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
    }

    public override ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest moduleRequest)
    {
        var spec = moduleRequest.Specifier;

        if (_bundle.SnapshotMultiFileLeaves is { Count: > 0 } leaves)
        {
            if (TryExtractNamespace(spec, out var specIndex, out var specInner))
            {
                if (specIndex < 0 || specIndex >= leaves.Count)
                {
                    throw new InvalidOperationException($"Snapshot module namespace '{spec}' is out of range.");
                }

                var leaf = leaves[specIndex];
                var resolvedInner = string.Equals(specInner, leaf.EntryPath, StringComparison.Ordinal)
                    ? leaf.EntryPath
                    : ResolveInsideBundle(leaf, referencingInner: null, specInner);
                return new ResolvedSpecifier(moduleRequest, $"{SnapshotPrefix}{specIndex}/{resolvedInner}", Uri: null, SpecifierType.Bare);
            }

            if (!string.IsNullOrEmpty(referencingModuleLocation) &&
                TryExtractNamespace(referencingModuleLocation, out var refIndex, out var refInner))
            {
                if (refIndex < 0 || refIndex >= leaves.Count)
                {
                    throw new InvalidOperationException($"Snapshot module namespace '{referencingModuleLocation}' is out of range.");
                }

                var leaf = leaves[refIndex];
                var resolvedInner = ResolveInsideBundle(leaf, refInner, spec);
                return new ResolvedSpecifier(moduleRequest, $"{SnapshotPrefix}{refIndex}/{resolvedInner}", Uri: null, SpecifierType.RelativeOrAbsolute);
            }

            if (string.IsNullOrEmpty(referencingModuleLocation) &&
                spec.StartsWith("template-", StringComparison.Ordinal) &&
                spec.EndsWith(".perga.js", StringComparison.Ordinal))
            {
                return new ResolvedSpecifier(moduleRequest, spec, Uri: null, SpecifierType.Bare);
            }

            throw new InvalidOperationException($"Module import '{spec}' is not permitted outside a template bundle.");
        }

        if (string.Equals(spec, _bundle.EntryPath, StringComparison.Ordinal))
        {
            return new ResolvedSpecifier(moduleRequest, _bundle.EntryPath, Uri: null, SpecifierType.Bare);
        }

        var resolved = ResolveInsideBundle(_bundle, referencingModuleLocation, spec);
        return new ResolvedSpecifier(moduleRequest, resolved, Uri: null, SpecifierType.RelativeOrAbsolute);
    }

    protected override string LoadModuleContents(Engine engine, ResolvedSpecifier resolved)
    {
        var key = resolved.Key;
        if (_bundle.SnapshotMultiFileLeaves is { Count: > 0 } leaves &&
            TryExtractNamespace(key, out var index, out var innerKey))
        {
            if (index >= 0 && index < leaves.Count && leaves[index].Files.TryGetValue(innerKey, out var leafContent))
            {
                return leafContent;
            }

            throw new InvalidOperationException($"Template module '{innerKey}' was not found in inherited template package.");
        }

        if (!_bundle.Files.TryGetValue(key, out var content))
        {
            throw new InvalidOperationException($"Template module '{key}' was not found in template bundle.");
        }

        return content;
    }

    private static string ResolveInsideBundle(TemplateBundle leaf, string? referencingInner, string spec)
    {
        var resolved = TemplateBundle.ResolveRelative(referencingInner, spec);
        if (!leaf.Files.ContainsKey(resolved) && leaf.Files.ContainsKey(resolved + ".js"))
        {
            resolved += ".js";
        }

        return resolved;
    }

    private static bool TryExtractNamespace(string location, out int index, out string innerPath)
    {
        index = -1;
        innerPath = string.Empty;
        if (!location.StartsWith(SnapshotPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var slash = location.IndexOf('/', SnapshotPrefix.Length);
        if (slash <= SnapshotPrefix.Length)
        {
            return false;
        }

        if (!int.TryParse(location[SnapshotPrefix.Length..slash], out index))
        {
            return false;
        }

        innerPath = location[(slash + 1)..];
        return true;
    }
}
