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
        return _snapshotCacheByHash.GetOrAdd(hash, _ => _policyVm.Compile(templateSnapshotSource));
    }

    public CompiledPolicy RegisterTemplate(string jsModuleSource)
    {
        if (string.IsNullOrWhiteSpace(jsModuleSource))
        {
            throw KernelException.InvalidArgument("Template JS module source cannot be empty.");
        }

        var compiled = _policyVm.Compile(jsModuleSource);
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

        var files = Directory.GetFiles(directoryPath, "*.perga.js", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        var count = 0;
        foreach (var file in files)
        {
            var source = File.ReadAllText(file, Encoding.UTF8);
            RegisterTemplate(source);
            count++;
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
