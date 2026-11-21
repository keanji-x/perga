using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class LegacyHookTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("1.5")]
    [InlineData("'2'")]
    [InlineData("null")]
    [InlineData("undefined")]
    public void Compile_RejectsInvalidToolNameVersion(string value)
    {
        var source = "export default {id:'invalid',title:'Invalid',toolNameVersion:" + value + "};";
        var error = Assert.Throws<InvalidOperationException>(() => new PolicyVm().Compile(source));
        Assert.Contains("template.toolNameVersion must be the integer 1 or 2.", error.Message);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void Compile_ExplicitToolNameVersionOverridesSnapshotDefault(int version, bool legacy)
    {
        var vm = new PolicyVm();
        var source = JsonSerializer.Serialize(new { id = "versioned", title = "Versioned", toolNameVersion = version });
        var compiled = vm.Compile(PolicyVm.WithToolNameVersion(source, legacy: !legacy));
        Assert.Equal(legacy, compiled.UsesLegacyBeforeToolNames);
        Assert.Equal(legacy, compiled.UsesLegacyAfterToolNames);
    }

    public static TheoryData<string, string> LegacyHooks => new()
    {
        { "guardToolCall", "guardToolCall: VALUE" },
        { "buildTurnContext", "buildTurnContext: VALUE" },
        { "report.customValidate", "report: {customValidate: VALUE}" },
        { "tasks.onSettled", "tasks: {onSettled: VALUE}" }
    };

    [Theory]
    [MemberData(nameof(LegacyHooks))]
    public void Compile_RejectsLegacyKeysRegardlessOfValueOrSnapshotPosition(string key, string member)
    {
        var vm = new PolicyVm();
        foreach (var value in new[] { "()=>Deny('legacy rule')", "null", "undefined" })
        {
            var legacy = "export default {id:'legacy',title:'Legacy'," + member.Replace("VALUE", value) + "};";
            const string clean = "export default {id:'clean',title:'Clean',before:()=>Allow(),report:{},tasks:{}};";
            foreach (var source in new[] { legacy, Snapshot(legacy, clean), Snapshot(clean, legacy), Snapshot(Snapshot(legacy, clean), clean) })
            {
                var error = Assert.Throws<InvalidOperationException>(() => vm.Compile(source));
                Assert.Contains(key, error.Message);
                foreach (var replacement in new[] { "before", "after", "turnContext", "headerView" })
                    Assert.Contains(replacement, error.Message);
            }
        }
    }

    private static string Snapshot(string parent, string child) =>
        JsonSerializer.Serialize(new { format = "perga.snapshot.v1", sources = new[] { parent, child } });

    [Theory]
    [InlineData("guardToolCall")]
    [InlineData("buildTurnContext")]
    [InlineData("report.customValidate")]
    [InlineData("tasks.onSettled")]
    public void Compile_RejectsNonEnumerableLegacyBaseKeysBeforeMerge(string key)
    {
        var parts = key.Split('.');
        var owner = parts.Length == 1 ? "def" : "def." + parts[0];
        var legacy = "const def = {id:'base',title:'Base',report:{},tasks:{}};" +
            "Object.defineProperty(" + owner + ", '" + parts[^1] + "', {value:()=>Deny('legacy rule')}); export default def;";
        var error = Assert.Throws<InvalidOperationException>(() => new PolicyVm().Compile(
            Snapshot(legacy, "export default {id:'child',title:'Child'};")));
        Assert.Contains(key, error.Message);
    }

    [Theory]
    [InlineData("guardToolCall")]
    [InlineData("buildTurnContext")]
    [InlineData("report.customValidate")]
    [InlineData("tasks.onSettled")]
    public void Compile_RejectsJsonLegacyKeys(string key)
    {
        var parts = key.Split('.');
        var definition = new Dictionary<string, object?> { ["id"] = "json", ["title"] = "JSON" };
        definition[parts[0]] = parts.Length == 1 ? null : new Dictionary<string, object?> { [parts[1]] = null };
        var error = Assert.Throws<InvalidOperationException>(() => new PolicyVm().Compile(JsonSerializer.Serialize(definition)));
        Assert.Contains(key, error.Message);
    }
}
