using System.Text.RegularExpressions;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateWidgetContractTests
{
    [Theory]
    [InlineData("report-widget.ts", "builtinTableViewSchema", "BuiltinTableView")]
    [InlineData("report-widget.ts", "builtinListViewSchema", "BuiltinListView")]
    [InlineData("report-editorial.ts", "editorialMetricSchema", "EditorialMetric")]
    [InlineData("report-editorial.ts", "editorialItemSchema", "EditorialListItem")]
    [InlineData("report-editorial.ts", "editorialCheckSchema", "EditorialCheck")]
    [InlineData("report-editorial.ts", "editorialExpectationSchema", "EditorialExpectation")]
    [InlineData("report-editorial.ts", "editorialMetricPointSchema", "EditorialMetricPoint")]
    public void TemplateViewDeclarationsMatchFrontendFieldsAndOptionality(string file, string schema, string type)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Perga.sln")))
            root = root.Parent;
        Assert.NotNull(root);

        var frontend = File.ReadAllText(Path.Combine(root.FullName, "fe", "core", "domain", file));
        var templateTypes = File.ReadAllText(Path.Combine(root.FullName, "templates", "perga-template.d.ts"));
        var schemaBody = Regex.Match(frontend, $@"(?ms)^\s*(?:export )?const {schema} = z\.(?:strictObject|object)\(\{{\r?\n(.*?)^\}}\);");
        var typeBody = Regex.Match(templateTypes, $@"(?ms)^export interface {type} \{{\r?\n(.*?)^\}}");
        Assert.True(schemaBody.Success, $"Cannot find {schema}.");
        Assert.True(typeBody.Success, $"Cannot find {type}.");

        var frontendFields = Regex.Matches(schemaBody.Groups[1].Value, @"(?m)^  (\w+): ([^\r\n]+),\r?$")
            .Select(m => (Name: m.Groups[1].Value, Optional: m.Groups[2].Value.EndsWith(".nullish()", StringComparison.Ordinal)));
        var templateFields = Regex.Matches(typeBody.Groups[1].Value, @"(?m)^  (\w+)(\?)?: ([^\r\n]+);\r?$")
            .Select(m =>
            {
                var optional = m.Groups[2].Success;
                Assert.Equal(optional, m.Groups[3].Value.EndsWith(" | null", StringComparison.Ordinal));
                return (Name: m.Groups[1].Value, Optional: optional);
            });

        Assert.NotEmpty(frontendFields);
        Assert.Equal(frontendFields.OrderBy(field => field.Name, StringComparer.Ordinal),
            templateFields.OrderBy(field => field.Name, StringComparer.Ordinal));
    }
}
