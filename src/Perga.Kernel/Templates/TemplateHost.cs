using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Perga.PolicyVm;
using Vm = Perga.PolicyVm.PolicyVm;

namespace Perga.Kernel.Templates;

public interface ITemplateCatalog
{
    IReadOnlyList<CompiledTemplateManifest> ListManifests();
    IReadOnlyList<CompiledPolicy> ListPolicies();
    CompiledPolicy? TryGetPolicy(string templateId);
    CompiledPolicy GetRequiredPolicy(string templateId);
    CompiledPolicy GetOrCompileSnapshot(string templateSnapshotSource);
    CompiledPolicy CompileSource(string source);
    CompiledPolicy RegisterTemplate(string jsModuleSource);
    int LoadFromDirectory(string directoryPath);
}

public class TemplateHost : ITemplateCatalog
{
    private const string CanonicalFileMarker = "// perga:tool-names-v2";
    private readonly IPolicyVm _policyVm;
    private readonly ConcurrentDictionary<string, CompiledPolicy> _policiesById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CompiledPolicy> _snapshotCacheByHash = new(StringComparer.Ordinal);

    public TemplateHost(IPolicyVm policyVm, string? initialTemplatesDirectory = null)
    {
        _policyVm = policyVm ?? throw new ArgumentNullException(nameof(policyVm));
        if (!string.IsNullOrWhiteSpace(initialTemplatesDirectory) && Directory.Exists(initialTemplatesDirectory))
        {
            LoadFromDirectory(initialTemplatesDirectory);
        }
    }

    public IReadOnlyList<CompiledTemplateManifest> ListManifests()
    {
        return _policiesById.Values
            .OrderBy(p => p.TemplateId, StringComparer.Ordinal)
            .Select(p => p.Manifest)
            .ToArray();
    }

    public IReadOnlyList<CompiledPolicy> ListPolicies()
    {
        return _policiesById.Values
            .OrderBy(p => p.TemplateId, StringComparer.Ordinal)
            .ToArray();
    }

    public CompiledPolicy? TryGetPolicy(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return null;
        }

        return _policiesById.TryGetValue(templateId, out var policy) ? policy : null;
    }

    public CompiledPolicy GetRequiredPolicy(string templateId)
    {
        var policy = TryGetPolicy(templateId);
        if (policy is null)
        {
            throw KernelException.NotFound($"Template '{templateId}' was not found in TemplateCatalog.");
        }

        return policy;
    }

    public CompiledPolicy GetOrCompileSnapshot(string templateSnapshotSource)
    {
        if (string.IsNullOrWhiteSpace(templateSnapshotSource))
        {
            throw KernelException.InvalidArgument("Template snapshot source cannot be empty.");
        }

        var hash = ComputeSha256Hex(templateSnapshotSource);
        return _snapshotCacheByHash.GetOrAdd(hash, _ =>
        {
            var source = Vm.WithToolNameVersion(templateSnapshotSource, legacy: true);
            return ResolveInheritance(_policyVm.Compile(source));
        });
    }

    public CompiledPolicy CompileSource(string source) => GetOrCompileSnapshot(Vm.WithToolNameVersion(source, legacy: false));

    internal static string Freeze(CompiledPolicy policy) => Vm.WithToolNameVersion(policy.JsSource, legacy: false);

    internal static string? LegacyReapplySource(CompiledPolicy? policy) =>
        policy is { UsesLegacyBeforeToolNames: true } or { UsesLegacyAfterToolNames: true } ? Freeze(policy) : null;

    internal static string? InspectSource(string? snapshot)
    {
        if (snapshot is null) return null;
        try { return Vm.ReadToolNameVersion(snapshot)?.Source ?? snapshot; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return snapshot; }
    }

    private CompiledPolicy ResolveInheritance(CompiledPolicy policy) =>
        policy.Manifest.Extends is { } baseId
            ? _policyVm.Compile(Vm.Inherit(GetRequiredPolicy(baseId), policy.JsSource))
            : policy;

    public CompiledPolicy RegisterTemplate(string jsModuleSource) =>
        Register(ResolveInheritance(_policyVm.Compile(Vm.WithToolNameVersion(jsModuleSource, legacy: false))));

    public CompiledPolicy RegisterFileSource(string source) => Register(ResolveInheritance(_policyVm.Compile(VersionFileSource(source))));

    private static string VersionFileSource(string source)
    {
        // Files can predate the upgrade. The marker opts a disk module into canonical names.
        using var reader = new StringReader(source.TrimStart());
        var legacy = reader.ReadLine()?.TrimEnd() != CanonicalFileMarker;
        return Vm.WithToolNameVersion(source, legacy);
    }

    private CompiledPolicy Register(CompiledPolicy compiled)
    {
        _policiesById[compiled.TemplateId] = compiled;
        _snapshotCacheByHash[ComputeSha256Hex(Freeze(compiled))] = compiled;
        return compiled;
    }

    public int LoadFromDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return 0;
        }

        var files = Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".perga.js", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        var pending = files.Select(file => VersionFileSource(File.ReadAllText(file, Encoding.UTF8)))
            .Select(_policyVm.Compile).ToList();
        var count = 0;
        while (pending.Count > 0)
        {
            var progressed = false;
            foreach (var definition in pending.ToArray())
            {
                if (definition.Manifest.Extends is { } baseId && TryGetPolicy(baseId) is null) continue;
                Register(ResolveInheritance(definition));
                pending.Remove(definition);
                count++;
                progressed = true;
            }
            if (!progressed) throw new InvalidOperationException("Template inheritance contains a cycle or an unavailable base.");
        }

        return count;
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}

public sealed class TemplateCatalog : TemplateHost
{
    public TemplateCatalog(IPolicyVm policyVm, string? initialTemplatesDirectory = null)
        : base(policyVm, initialTemplatesDirectory)
    {
    }
}
