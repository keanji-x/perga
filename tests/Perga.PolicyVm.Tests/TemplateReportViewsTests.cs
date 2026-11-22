using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateReportViewsTests
{
    private readonly PolicyVm _vm = new();
    private static readonly JsonElement EmptyKernel = Json("""{"tasks":{},"sessions":{}}""");

    [Fact]
    public void ViewsReceiveEffectiveReadOnlyInputsDuringCompileAndProjection()
    {
        var policy = _vm.Compile("""
            export default defineTemplate({
              id: "views-with-inputs",
              title: "Views with inputs",
              inputs: { label: { type: "string", default: "Default" } },
              data: { initial: { title: "Report" } },
              report: { views: {
                usingInputs: (data, kernel, inputs) => {
                  if (!Object.isFrozen(inputs) || typeof inputs.label !== "string")
                    throw new Error("Expected read-only effective inputs");
                  return { title: data.title, label: inputs.label, tasks: Object.keys(kernel.tasks) };
                },
                twoArgs: (data, kernel) => ({ title: data.title, tasks: Object.keys(kernel.tasks) })
              } }
            });
            """);

        var inputs = Json("""{"label":"Explicit"}""");
        var data = _vm.InitData(policy, inputs);
        var kernel = Json("""{"tasks":{"review":{}}}""");
        var projected = _vm.ProjectViews(policy, inputs, data, kernel);
        Assert.Empty(projected.Diagnostics);
        Assert.True(JsonElement.DeepEquals(
            Json("""{"title":"Report","label":"Explicit","tasks":["review"]}"""), projected.Views["usingInputs"]));
        Assert.True(JsonElement.DeepEquals(
            Json("""{"title":"Report","tasks":["review"]}"""), projected.Views["twoArgs"]));

        var withDefaults = _vm.ProjectViews(policy, Json("{}"), data, EmptyKernel);
        Assert.Empty(withDefaults.Diagnostics);
        Assert.Equal("Default", withDefaults.Views["usingInputs"].GetProperty("label").GetString());
    }

    [Fact]
    public void EquityResearchPreservesV1TickerDataAndUsesCurrentInputsForViews()
    {
        var policy = CompileBuiltin("equity-research");
        var oldData = Json("""
            {
              "ticker":"OLD",
              "valuation":{"unit":"$","targets":[]},
              "events":[{"id":"review","date":"2026-11-23","title":"Review","verdict":"beat","assign":"Reviewer",
                         "triggerAt":"After close","expects":[{"label":"Result","expected":"2","actual":"3","status":"beat"}]}]
            }
            """);
        var inputs = Json("""{"ticker":"NEW"}""");
        var fresh = _vm.InitData(policy, inputs);
        Assert.False(fresh.TryGetProperty("ticker", out _));

        var upgrade = _vm.ValidateAndPatchData(policy, inputs, oldData, Json("{}"), [],
            "2026-11-23T00:00:00Z", "template:upgrade");
        Assert.True(upgrade.Valid);
        Assert.True(JsonElement.DeepEquals(oldData, upgrade.NextData));

        var projected = _vm.ProjectViews(policy, inputs, upgrade.NextData, EmptyKernel);
        Assert.Empty(projected.Diagnostics);
        Assert.Equal("NEW", projected.Views["valuationAxis"].GetProperty("ticker").GetString());
        var item = Assert.Single(projected.Views["eventChronicle"].GetProperty("items").EnumerateArray());
        Assert.Equal("超预期", item.GetProperty("badge").GetString());
        Assert.Equal("good", item.GetProperty("tone").GetString());
        Assert.Equal("Reviewer", item.GetProperty("meta").GetString());
        Assert.Equal("After close", item.GetProperty("subtitle").GetString());
        Assert.False(item.TryGetProperty("verdict", out _));
        var row = Assert.Single(item.GetProperty("table").GetProperty("rows").EnumerateArray());
        Assert.Equal("3", row.GetProperty("actual").GetString());
        Assert.Equal("good", row.GetProperty("status").GetProperty("tone").GetString());
    }

    [Fact]
    public void OkrLoopUsesTheGenericFourColumnTable()
    {
        var policy = CompileBuiltin("okr-loop");
        var inputs = Json("{}");
        var data = Json("""
            {"krs":[{"code":"KR1","label":"**Latency**","tag":"CI","current":"8 min","targetLabel":"≤ 8 min",
                     "status":"met","updatedAtDisplay":"Today"}]}
            """);
        var projected = _vm.ProjectViews(policy, inputs, data, EmptyKernel);
        Assert.Empty(projected.Diagnostics);
        var ledger = projected.Views["krLedger"];
        Assert.Equal(["Key Result", "当前", "目标", "更新时间"],
            ledger.GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("label").GetString()));
        var row = Assert.Single(ledger.GetProperty("rows").EnumerateArray());
        Assert.True(row.GetProperty("result").GetProperty("markdown").GetBoolean());
        Assert.Equal("KR1", row.GetProperty("result").GetProperty("code").GetString());
        Assert.True(row.GetProperty("current").GetProperty("check").GetBoolean());
        Assert.Equal("good", row.GetProperty("current").GetProperty("tone").GetString());
        Assert.Equal("Today", row.GetProperty("updatedAt").GetString());
    }

    private CompiledPolicy CompileBuiltin(string template)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../templates", template));
        return _vm.Compile(TemplateBundle.PackDirectory(path));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
