using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Perga.PolicyVm;

namespace Perga.Kernel.Templates;

public interface ITemplateCatalog
{
    IReadOnlyList<CompiledTemplateManifest> ListManifests();
    IReadOnlyList<CompiledPolicy> ListPolicies();
    CompiledPolicy? TryGetPolicy(string templateId);
    CompiledPolicy GetRequiredPolicy(string templateId);
    CompiledPolicy GetOrCompileSnapshot(string templateSnapshotSource);
    CompiledPolicy RegisterTemplate(string jsModuleSource);
    int LoadFromDirectory(string directoryPath);
}

public class TemplateHost : ITemplateCatalog
{
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
            var policy = _policyVm.Compile(templateSnapshotSource);
            return policy.Manifest.Extends is { } baseId
                ? _policyVm.Compile(global::Perga.PolicyVm.PolicyVm.Inherit(GetRequiredPolicy(baseId), templateSnapshotSource)) : policy;
        });
    }

    public CompiledPolicy RegisterTemplate(string jsModuleSource)
    {
        if (string.IsNullOrWhiteSpace(jsModuleSource))
        {
            throw KernelException.InvalidArgument("Template JS module source cannot be empty.");
        }

        var compiled = _policyVm.Compile(jsModuleSource);
        if (compiled.Manifest.Extends is { } baseId)
            compiled = _policyVm.Compile(global::Perga.PolicyVm.PolicyVm.Inherit(GetRequiredPolicy(baseId), jsModuleSource));
        _policiesById[compiled.TemplateId] = compiled;
        _snapshotCacheByHash[ComputeSha256Hex(jsModuleSource)] = compiled;
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

        var pending = files.Select(file => File.ReadAllText(file, Encoding.UTF8))
            .Select(source => (Source: source, Policy: _policyVm.Compile(source))).ToList();
        var count = 0;
        while (pending.Count > 0)
        {
            var progressed = false;
            foreach (var entry in pending.ToArray())
            {
                var definition = entry.Policy;
                if (definition.Manifest.Extends is { } baseId && TryGetPolicy(baseId) is null) continue;
                var compiled = definition.Manifest.Extends is { } parentId
                    ? _policyVm.Compile(global::Perga.PolicyVm.PolicyVm.Inherit(GetRequiredPolicy(parentId), entry.Source)) : definition;
                _policiesById[compiled.TemplateId] = compiled;
                _snapshotCacheByHash[ComputeSha256Hex(compiled.JsSource)] = compiled;
                pending.Remove(entry);
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
