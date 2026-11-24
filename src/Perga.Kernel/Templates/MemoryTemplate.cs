using Perga.PolicyVm;

namespace Perga.Kernel.Templates;

internal static class MemoryTemplate
{
    internal const string Id = "memory";

    private static readonly Lazy<string> Source = new(() =>
    {
        var assembly = typeof(MemoryTemplate).Assembly;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "template.perga.js", "schema.js", "views.js", "report.md", "widgets/entries.html" })
        {
            using var stream = assembly.GetManifestResourceStream($"Perga.Templates.Memory.{name}")
                ?? throw new InvalidOperationException($"Missing built-in Memory template file '{name}'.");
            using var reader = new StreamReader(stream);
            files.Add(name, reader.ReadToEnd());
        }
        return TemplateBundle.PackFiles(files, "template.perga.js", Id);
    });

    internal static CompiledPolicy Resolve(ITemplateCatalog catalog) => catalog.GetOrCompileSnapshot(Source.Value);
}
