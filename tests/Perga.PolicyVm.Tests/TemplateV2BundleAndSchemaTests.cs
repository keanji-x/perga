using System.Text.Json;
using Perga.PolicyVm;
using Xunit;

namespace Perga.PolicyVm.Tests;

public sealed class TemplateV2BundleAndSchemaTests
{
    private readonly PolicyVm _vm = new();

    private static string BuildSampleBundle(string? overrideReportMd = null)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["template.perga.js"] = """
                import { schema, initial } from "./schema.js";
                import { views } from "./views.js";

                export default defineTemplate({
                  id: "sample-v2",
                  title: "Sample V2 Package",
                  inputs: {
                    ticker: { type: "string", default: "NVDA" }
                  },
                  setup(inputs) {
                    return {
                      data: { schema, initial: () => initial(inputs) },
                      report: {
                        template: "./report.md",
                        views
                      },
                      flow: [
                        {
                          id: "daily-check",
                          when: { cron: "0 9 * * *" },
                          do: { kind: "wake", session: "lead", prompt: "Daily check for ${inputs.ticker}" }
                        }
                      ]
                    };
                  }
                });
                """,
            ["schema.js"] = """
                export const schema = (t) => ({
                  ticker: t.string(),
                  price: t.number(),
                  events: t.array(t.object({
                    id: t.id(),
                    date: t.isoDate(),
                    verdict: t.enum(["pending", "beat", "inline", "miss"]),
                    taskRef: t.ref("task").optional(),
                  }))
                });

                export const initial = (inputs) => ({
                  ticker: inputs.ticker,
                  price: 137.5,
                  events: [
                    { id: "q4", date: "2026-11-26", verdict: "pending" }
                  ]
                });
                """,
            ["views.js"] = """
                export const views = {
                  priceAxis: (data) => ({ current: data.price, ticker: data.ticker }),
                  eventList: (data, kernel) => ({
                    items: data.events.map((e) => ({
                      id: e.id,
                      title: e.id,
                      status: "pending",
                      date: e.date,
                      verdict: e.verdict,
                      model: e.taskRef && kernel.tasks[e.taskRef] ? kernel.tasks[e.taskRef].model : null
                    }))
                  })
                };
                """,
            ["report.md"] = overrideReportMd ?? """
                # Price

                ```perga-block widget
                {"use":"./widgets/axis.html","view":"priceAxis","chrome":"seamless"}
                ```

                # Events

                ```perga-block widget
                {"use":"builtin:list","view":"eventList"}
                ```
                """,
            ["widgets/axis.html"] = "<!doctype html><html><body>axis</body></html>"
        };

        return TemplateBundle.BundleHeader + JsonSerializer.Serialize(new { entry = "template.perga.js", files });
    }

    [Fact]
    public void Compiles_MultiFile_Bundle_And_Projects_Views_With_Kernel_Facts()
    {
        var bundleSource = BuildSampleBundle();
        var compiled = _vm.Compile(bundleSource);

        Assert.Equal("sample-v2", compiled.TemplateId);
        Assert.Contains("perga-block widget", compiled.Manifest.InitialReportMarkdown);
        Assert.Equal(["priceAxis", "eventList"], compiled.Manifest.ViewNames);
        Assert.Equal("Daily check for ${inputs.ticker}", Assert.Single(_vm.ResolveFlow(compiled, JsonSerializer.Deserialize<JsonElement>("{}"))).Do.Prompt);

        var inputs = JsonDocument.Parse("""{"ticker":"NVDA"}""").RootElement;
        var init = _vm.InitData(compiled, inputs);

        var patch = JsonDocument.Parse("""
            {
              "price": 142.0,
              "events": [
                { "id": "q4", "date": "2026-11-26", "verdict": "beat", "taskRef": "task-q4" }
              ]
            }
            """).RootElement;

        var patched = _vm.ValidateAndPatchData(
            compiled, inputs, init, patch,
            knownTaskKeys: ["task-q4"],
            nowIso: "2026-11-26T22:00:00Z",
            source: "session:ses_1");

        Assert.True(patched.Valid);
        Assert.Empty(patched.Errors);

        var kernelSnapshot = JsonDocument.Parse("""
            {
              "tasks": {
                "task-q4": { "model": "claude-opus-4", "sessionId": "ses_q4", "status": "done" }
              }
            }
            """).RootElement;

        var projection = _vm.ProjectViews(compiled, inputs, patched.NextData, kernelSnapshot);
        Assert.Empty(projection.Diagnostics);
        Assert.Equal(142.0, projection.Views["priceAxis"].GetProperty("current").GetDouble());
        Assert.Equal("claude-opus-4", projection.Views["eventList"].GetProperty("items")[0].GetProperty("model").GetString());
        Assert.True(projection.Widgets.ContainsKey("./widgets/axis.html"));
    }

    [Fact]
    public void ValidateAndPatchData_Rejects_Invalid_Enum_Date_And_Meta_Tampering_FailClosed()
    {
        var compiled = _vm.Compile(BuildSampleBundle());
        var inputs = JsonDocument.Parse("{}").RootElement;
        var init = _vm.InitData(compiled, inputs);

        var badPatch = JsonDocument.Parse("""
            {
              "events": [
                { "id": "q4", "date": "11月26日", "verdict": "还不错" }
              ]
            }
            """).RootElement;

        var res = _vm.ValidateAndPatchData(
            compiled, inputs, init, badPatch,
            knownTaskKeys: null,
            nowIso: "2026-11-26T22:00:00Z",
            source: "session:ai");

        Assert.False(res.Valid);
        Assert.Contains(res.Errors, e => e.Path == "events[0].date" && e.Message.Contains("YYYY-MM-DD"));
        Assert.Contains(res.Errors, e => e.Path == "events[0].verdict" && e.Message.Contains("pending, beat, inline, miss"));
        // Fail-Closed: returns original data untouched
        Assert.Equal("pending", res.NextData.GetProperty("events")[0].GetProperty("verdict").GetString());
    }

    [Fact]
    public void Compile_Rejects_Missing_View_Or_Missing_Widget_Asset_In_ReportMd()
    {
        var missingViewBundle = BuildSampleBundle("""
            # Bad
            ```perga-block widget
            {"use":"builtin:list","view":"doesNotExist"}
            ```
            """);
        var ex1 = Assert.Throws<InvalidOperationException>(() => _vm.Compile(missingViewBundle));
        Assert.Contains("doesNotExist", ex1.Message);

        var missingWidgetBundle = BuildSampleBundle("""
            # Bad
            ```perga-block widget
            {"use":"./widgets/missing.html","view":"priceAxis"}
            ```
            """);
        var ex2 = Assert.Throws<InvalidOperationException>(() => _vm.Compile(missingWidgetBundle));
        Assert.Contains("missing.html", ex2.Message);
    }

    [Theory]
    [InlineData("agents-delivery")]
    [InlineData("equity-research")]
    [InlineData("okr-loop")]
    public void Real_MultiFile_Templates_In_Repo_Pack_Compile_And_Project_Cleanly(string packageName)
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "templates", packageName));
        Assert.True(Directory.Exists(dir), $"Expected template directory '{dir}' to exist.");

        var packed = TemplateBundle.PackDirectory(dir);
        var compiled = _vm.Compile(packed);
        Assert.Equal(packageName, compiled.TemplateId);

        var inputs = JsonDocument.Parse("{}").RootElement;
        var init = _vm.InitData(compiled, inputs);
        var emptyKernel = JsonDocument.Parse("""{"tasks":{},"sessions":{}}""").RootElement;
        var projection = _vm.ProjectViews(compiled, inputs, init, emptyKernel);

        Assert.Empty(projection.Diagnostics);
        if (packageName == "agents-delivery") Assert.Empty(projection.Views);
        else Assert.NotEmpty(projection.Views);
    }

    [Fact]
    public void SnapshotReuseKeepsModuleStateEvaluationLocal()
    {
        var source = TemplateBundle.BundleHeader + JsonSerializer.Serialize(new
        {
            files = new Dictionary<string, string>
            {
                ["template.perga.js"] = """
                    import { next } from "./counter.js";
                    export default defineTemplate({
                      id: "snapshot-counter", title: "Counter", initialState: () => ({ count: next() }),
                    });
                    """,
                ["counter.js"] = "let count = 0; export const next = () => ++count;",
            },
        });
        var compiled = _vm.Compile(PolicyVm.WithToolNameVersion(source, legacy: true));
        var reconstructed = new CompiledPolicy(compiled.TemplateId, compiled.JsSource, compiled.Manifest);
        foreach (var policy in new[] { compiled, compiled, reconstructed, reconstructed })
        {
            Assert.Equal(1, _vm.InitState(policy, default).Output.GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public void ValidateAndPatchData_Rejects_MetaTampering_PrototypePollution_InvalidDates_And_BadRefs_FailClosed()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["template.perga.js"] = """
                export default defineTemplate({
                  id: "sec-test",
                  title: "Security Test",
                  setup() {
                    return {
                      data: {
                        schema: (t) => ({
                          doc: t.ref("file").optional(),
                          task: t.ref("task").optional(),
                          day: t.isoDate().optional(),
                          nested: t.object({
                            title: t.string().optional(),
                          }).optional(),
                        }),
                        initial: () => ({}),
                      },
                      report: { template: "./report.md", views: { v: () => ({ items: [] }) } },
                    };
                  },
                });
                """,
            ["report.md"] = "# Overview\n\n```perga-block widget\n{\"use\":\"builtin:list\",\"view\":\"v\"}\n```\n",
        };
        var bundle = TemplateBundle.BundleHeader + JsonSerializer.Serialize(new { entry = "template.perga.js", files });
        var compiled = _vm.Compile(bundle);
        var inputs = JsonDocument.Parse("{}").RootElement;
        var init = _vm.InitData(compiled, inputs);

        // 1. Nested _meta tampering rejected
        var metaPatch = JsonDocument.Parse("""{"nested":{"_meta":{"updatedAt":"1999-01-01"}}}""").RootElement;
        var rMeta = _vm.ValidateAndPatchData(compiled, inputs, init, metaPatch, [], "2026-11-26T00:00:00Z", "session:ai");
        Assert.False(rMeta.Valid);
        Assert.Contains(rMeta.Errors, e => e.Message.Contains("_meta"));

        // 2. Prototype pollution keys rejected
        var protoPatch = JsonDocument.Parse("""{"nested":{"__proto__":{"polluted":true}}}""").RootElement;
        var rProto = _vm.ValidateAndPatchData(compiled, inputs, init, protoPatch, [], "2026-11-26T00:00:00Z", "session:ai");
        Assert.False(rProto.Valid);
        Assert.Contains(rProto.Errors, e => e.Message.Contains("__proto__"));

        // 3. Impossible calendar date rejected
        var datePatch = JsonDocument.Parse("""{"day":"2026-02-31"}""").RootElement;
        var rDate = _vm.ValidateAndPatchData(compiled, inputs, init, datePatch, [], "2026-11-26T00:00:00Z", "session:ai");
        Assert.False(rDate.Valid);

        // 4. File ref traversal / scheme / query rejected
        foreach (var badFile in new[] { "../etc/passwd", " /etc/passwd", "file:///etc/passwd", "C:/secret.txt", "docs/a.md?x=1", "docs/a.md#frag" })
        {
            var filePatch = JsonDocument.Parse(JsonSerializer.Serialize(new { doc = badFile })).RootElement;
            var rFile = _vm.ValidateAndPatchData(compiled, inputs, init, filePatch, [], "2026-11-26T00:00:00Z", "session:ai");
            Assert.False(rFile.Valid, $"Expected '{badFile}' to be rejected");
        }

        // 5. Task ref on 0-task track fails closed (empty list is NOT bypassed)
        var taskPatch = JsonDocument.Parse("""{"task":"nonexistent-task"}""").RootElement;
        var rTask = _vm.ValidateAndPatchData(compiled, inputs, init, taskPatch, knownTaskKeys: [], "2026-11-26T00:00:00Z", "session:ai");
        Assert.False(rTask.Valid);

        // 6. Non-object patch returns structured validation error instead of crashing
        var arrayPatch = JsonDocument.Parse("""[1,2,3]""").RootElement;
        var rArray = _vm.ValidateAndPatchData(compiled, inputs, init, arrayPatch, [], "2026-11-26T00:00:00Z", "session:ai");
        Assert.False(rArray.Valid);
    }

    [Fact]
    public void ResolveRoutingRules_Respects_Custom_Setup_Inputs_And_TargetAccess()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "templates", "agents-delivery"));
        var compiled = _vm.Compile(TemplateBundle.PackDirectory(dir));
        var customInputs = JsonDocument.Parse("""{"reviewModel":"o3-pro","implModel":"claude-sonnet-4-5"}""").RootElement;

        var rules = _vm.ResolveRoutingRules(compiled, customInputs);
        var reviewRule = Assert.Single(rules, r => string.Equals(r.When.Role, "review", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("o3-pro", reviewRule.Model);
        Assert.True(reviewRule.Enforce);
        Assert.Equal("read_only", reviewRule.TargetAccess);
    }

    [Fact]
    public void TemplateBundle_Rejects_Duplicate_Normalized_Paths_And_Disallowed_Extensions()
    {
        var dupJson = TemplateBundle.BundleHeader + """{"entry":"template.perga.js","files":{"template.perga.js":"export default {}","./template.perga.js":"export default {}"}}""";
        Assert.Throws<InvalidOperationException>(() => TemplateBundle.Parse(dupJson));

        var badExtJson = TemplateBundle.BundleHeader + """{"entry":"template.perga.js","files":{"template.perga.js":"export default {}","evil.sh":"echo hi"}}""";
        Assert.Throws<InvalidOperationException>(() => TemplateBundle.Parse(badExtJson));
    }

    [Fact]
    public void SetupInputs_LifecycleHooks_AreExecutedAtRuntimeWithActualInputs()
    {
        const string source = """
            export default defineTemplate({
              id: "setup-hooks",
              title: "Setup Hooks",
              inputs: {
                mode: { type: "string", default: "allow" },
              },
              setup(inputs) {
                return {
                  before(action, ctx) {
                    ctx.state.beforeCalls = (ctx.state.beforeCalls || 0) + 1;
                    if (inputs.mode === "deny" && action.type === "task.dispatch") {
                      return Deny("blocked by setup mode=" + inputs.mode);
                    }
                    return Allow();
                  },
                  after(event, ctx) {
                    ctx.state.afterCalls = (ctx.state.afterCalls || 0) + 1;
                    return "after:" + inputs.mode + ":" + event.type;
                  },
                  turnContext(ctx) {
                    ctx.state.turnCalls = (ctx.state.turnCalls || 0) + 1;
                    return "ctx:" + inputs.mode;
                  },
                  headerView() {
                    return { badges: [{ label: "mode=" + inputs.mode, tone: "accent" }] };
                  },
                };
              },
            });
            """;

        var compiled = _vm.Compile(source);
        Assert.True(compiled.Manifest.HasHeaderView);

        var denyInputs = JsonDocument.Parse("""{"mode":"deny"}""").RootElement;
        var emptyState = JsonDocument.Parse("{}").RootElement;
        var payload = JsonDocument.Parse("""{"role":"review"}""").RootElement;

        var beforeRes = _vm.Before(compiled, "task.dispatch", "trk_1", payload, denyInputs, emptyState);
        var beforeDeny = Assert.IsType<GuardDecision.Deny>(beforeRes.Output);
        Assert.Contains("blocked by setup mode=deny", beforeDeny.Reason);
        Assert.Equal(1, beforeRes.NextState.GetProperty("beforeCalls").GetInt32());

        var afterRes = _vm.After(compiled, "task.settled", "trk_1", payload, denyInputs, beforeRes.NextState);
        Assert.Equal("after:deny:task.settled", afterRes.Output);
        Assert.Equal(1, afterRes.NextState.GetProperty("afterCalls").GetInt32());

        var turnRes = _vm.PrepareTurn(
            compiled,
            new PolicyTurnContext("trk_1", "ses_1", IsLead: true, BoundTaskId: null, ContextUsageRatio: 0.1, Inputs: denyInputs),
            afterRes.NextState);
        Assert.Equal("ctx:deny", turnRes.Output.Context);
        Assert.IsType<GuardDecision.Allow>(turnRes.Output.Decision);
        Assert.Equal(1, turnRes.NextState.GetProperty("turnCalls").GetInt32());
        Assert.Equal(2, turnRes.NextState.GetProperty("beforeCalls").GetInt32());

        var header = _vm.EvaluateHeaderView(compiled, new PolicyHeaderViewContext("trk_1", denyInputs), turnRes.NextState);
        Assert.NotNull(header);
        Assert.Equal("mode=deny", Assert.Single(header!.Badges!).Label);
    }

    [Fact]
    public void ExtendsInheritance_SupportsBundledParent_BundledChild_AndFrozenSnapshotReload()
    {
        var singleParentSource = """
            export default defineTemplate({
              id: "single-parent",
              title: "Single Parent",
              plugins: ["base-plugin"],
              before(action) {
                return action.type === "task.dispatch" ? Deny("parent denied") : Allow();
              },
            });
            """;
        var singleParent = _vm.Compile(singleParentSource);

        var childBundleDir = Path.Combine(Path.GetTempPath(), $"perga-inherit-child-{Guid.NewGuid():N}");
        var parentBundleDir = Path.Combine(Path.GetTempPath(), $"perga-inherit-parent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(parentBundleDir, "widgets"));
        Directory.CreateDirectory(childBundleDir);
        try
        {
            // Case A: Single-file parent + multi-file bundled child
            File.WriteAllText(Path.Combine(childBundleDir, "helper.js"), "export const childTag = 'from-child-helper';");
            File.WriteAllText(Path.Combine(childBundleDir, "template.perga.js"), """
                import { childTag } from "./helper.js";
                export default defineTemplate({
                  id: "bundled-child",
                  title: "Bundled Child",
                  extends: "single-parent",
                  plugins: [childTag],
                });
                """);

            var bundledChildSource = TemplateBundle.PackDirectory(childBundleDir);
            var inheritedA = _vm.Compile(PolicyVm.Inherit(singleParent, bundledChildSource));
            Assert.Equal(["base-plugin", "from-child-helper"], inheritedA.Manifest.DefaultPlugins);
            var beforeA = _vm.Before(
                inheritedA,
                "task.dispatch",
                "trk_1",
                JsonDocument.Parse("{}").RootElement,
                JsonDocument.Parse("{}").RootElement,
                JsonDocument.Parse("{}").RootElement);
            var beforeADeny = Assert.IsType<GuardDecision.Deny>(beforeA.Output);
            Assert.Equal("parent denied", beforeADeny.Reason);

            // Frozen snapshot reloads identically on restart
            var reloadedA = _vm.Compile(inheritedA.JsSource);
            Assert.Equal(["base-plugin", "from-child-helper"], reloadedA.Manifest.DefaultPlugins);

            // Case B: Bundled parent (with helper.js, report.md, widget) + bundled child (with its OWN helper.js!)
            File.WriteAllText(Path.Combine(parentBundleDir, "helper.js"), "export const parentTag = 'from-parent-helper';");
            File.WriteAllText(Path.Combine(parentBundleDir, "report.md"), """
                # Overview

                ```perga-block widget
                {"use":"./widgets/chart.html","view":"summary"}
                ```
                """);
            File.WriteAllText(Path.Combine(parentBundleDir, "widgets", "chart.html"), "<div>parent-widget</div>");
            File.WriteAllText(Path.Combine(parentBundleDir, "template.perga.js"), """
                import { parentTag } from "./helper.js";
                export default defineTemplate({
                  id: "bundled-parent",
                  title: "Bundled Parent",
                  plugins: [parentTag],
                  report: {
                    template: "./report.md",
                    views: {
                      summary: (data) => ({ tag: parentTag, count: data.count || 0 }),
                    },
                  },
                });
                """);

            var bundledParent = _vm.Compile(TemplateBundle.PackDirectory(parentBundleDir));
            var inheritedB = _vm.Compile(PolicyVm.Inherit(bundledParent, bundledChildSource));
            Assert.Equal(["from-parent-helper", "from-child-helper"], inheritedB.Manifest.DefaultPlugins);
            Assert.Contains("widgets/chart.html", inheritedB.Manifest.InitialReportMarkdown);

            var projectedB = _vm.ProjectViews(
                inheritedB,
                JsonDocument.Parse("{}").RootElement,
                JsonDocument.Parse("""{"count":7}""").RootElement,
                JsonDocument.Parse("""{"tasks":{},"sessions":{}}""").RootElement);
            Assert.Equal("from-parent-helper", projectedB.Views["summary"].GetProperty("tag").GetString());
            Assert.Equal(7, projectedB.Views["summary"].GetProperty("count").GetInt32());
            Assert.Contains("widgets/chart.html", projectedB.Widgets.Keys);

            // Reload frozen snapshot with two isolated bundles
            var reloadedB = _vm.Compile(inheritedB.JsSource);
            Assert.Equal(["from-parent-helper", "from-child-helper"], reloadedB.Manifest.DefaultPlugins);
        }
        finally
        {
            Directory.Delete(childBundleDir, recursive: true);
            Directory.Delete(parentBundleDir, recursive: true);
        }
    }

    [Fact]
    public void SchemaValidation_IsImmuneToTemplatePrototypePollution()
    {
        const string hostileSource = """
            Array.prototype.push = function () { return 0; };
            Object.keys = function () { return []; };
            JSON.stringify = function () { return "{}"; };
            Set.prototype.has = function () { return true; };

            export default defineTemplate({
              id: "hostile-proto",
              title: "Hostile Proto",
              data: {
                schema: (t) => ({
                  price: t.number(),
                  task: t.ref("task").optional(),
                }),
                initial: () => ({ price: 10 }),
              },
            });
            """;

        var compiled = _vm.Compile(hostileSource);
        var inputs = JsonDocument.Parse("{}").RootElement;
        var init = _vm.InitData(compiled, inputs);

        var badType = _vm.ValidateAndPatchData(
            compiled,
            inputs,
            init,
            JsonDocument.Parse("""{"price":"not-a-number"}""").RootElement,
            knownTaskKeys: [],
            nowIso: "2026-11-26T00:00:00Z",
            source: "session:ai");
        Assert.False(badType.Valid);
        Assert.Contains(badType.Errors, e => e.Path == "price");

        var badMeta = _vm.ValidateAndPatchData(
            compiled,
            inputs,
            init,
            JsonDocument.Parse("""{"_meta":{"updatedAt":"1999-01-01"}}""").RootElement,
            knownTaskKeys: [],
            nowIso: "2026-11-26T00:00:00Z",
            source: "session:ai");
        Assert.False(badMeta.Valid);

        var badProto = _vm.ValidateAndPatchData(
            compiled,
            inputs,
            init,
            JsonDocument.Parse("""{"__proto__":{"polluted":true}}""").RootElement,
            knownTaskKeys: [],
            nowIso: "2026-11-26T00:00:00Z",
            source: "session:ai");
        Assert.False(badProto.Valid);

        var badTask = _vm.ValidateAndPatchData(
            compiled,
            inputs,
            init,
            JsonDocument.Parse("""{"task":"ghost-task"}""").RootElement,
            knownTaskKeys: [],
            nowIso: "2026-11-26T00:00:00Z",
            source: "session:ai");
        Assert.False(badTask.Valid);

        var ok = _vm.ValidateAndPatchData(
            compiled,
            inputs,
            init,
            JsonDocument.Parse("""{"price":42}""").RootElement,
            knownTaskKeys: [],
            nowIso: "2026-11-26T00:00:00Z",
            source: "session:ai");
        Assert.True(ok.Valid);
        Assert.Equal(42, ok.NextData.GetProperty("price").GetDouble());
        Assert.Equal("session:ai", ok.NextData.GetProperty("_meta").GetProperty("source").GetString());
    }

    [Fact]
    public void InitData_ValidatesAndStampsActualRuntimeInputs_FailClosed()
    {
        const string source = """
            export default defineTemplate({
              id: "init-runtime-check",
              title: "Init Runtime Check",
              inputs: {
                count: { type: "number", default: 1 },
              },
              data: {
                schema: (t) => ({
                  count: t.number(),
                  taskRef: t.ref("task").optional(),
                }),
                initial: (inputs) => {
                  if (inputs.count === 2) return { count: "bad" };
                  if (inputs.count === 3) return { count: 3, _meta: { updatedAt: "1999-01-01" } };
                  if (inputs.count === 4) return { count: 4, taskRef: "ghost-task" };
                  return { count: inputs.count };
                },
              },
            });
            """;

        var compiled = _vm.Compile(source);

        var invalidTypeErr = Assert.Throws<InvalidOperationException>(() =>
            _vm.InitData(compiled, JsonDocument.Parse("""{"count":2}""").RootElement));
        Assert.Contains("schema validation", invalidTypeErr.Message);

        var metaErr = Assert.Throws<InvalidOperationException>(() =>
            _vm.InitData(compiled, JsonDocument.Parse("""{"count":3}""").RootElement));
        Assert.Contains("_meta", metaErr.Message);

        var taskRefErr = Assert.Throws<InvalidOperationException>(() =>
            _vm.InitData(compiled, JsonDocument.Parse("""{"count":4}""").RootElement));
        Assert.Contains("ghost-task", taskRefErr.Message);

        var valid = _vm.InitData(compiled, JsonDocument.Parse("""{"count":5}""").RootElement);
        Assert.Equal(5, valid.GetProperty("count").GetInt32());
        Assert.Equal("template:initial", valid.GetProperty("_meta").GetProperty("source").GetString());
        Assert.False(string.IsNullOrWhiteSpace(valid.GetProperty("_meta").GetProperty("updatedAt").GetString()));
    }

    [Fact]
    public void ExtendsInheritance_ComposesSetupHooksAndAuthorityRulesAcrossParentAndChild()
    {
        const string parentSource = """
            export default defineTemplate({
              id: "setup-parent",
              title: "Setup Parent",
              inputs: {
                reviewModel: { type: "string", default: "safe-model" },
              },
              setup(inputs) {
                return {
                  before(action) {
                    return action.type === "task.dispatch" ? Deny("parent denied") : Allow();
                  },
                  after() {
                    return "parent note";
                  },
                  tasks: {
                    routing: [
                      { when: { role: "review" }, model: inputs.reviewModel, access: "read_only", enforce: true },
                    ],
                  },
                };
              },
            });
            """;

        const string childSource = """
            export default defineTemplate({
              id: "setup-child",
              title: "Setup Child",
              extends: "setup-parent",
              setup() {
                return {
                  turnContext() {
                    return "child context";
                  },
                };
              },
            });
            """;

        var parent = _vm.Compile(parentSource);
        var inherited = _vm.Compile(PolicyVm.Inherit(parent, childSource));
        var reloaded = _vm.Compile(inherited.JsSource);

        foreach (var policy in new[] { inherited, reloaded })
        {
            var routeRule = Assert.Single(policy.Manifest.Routing);
            Assert.Equal("safe-model", routeRule.Model);
            Assert.True(routeRule.Enforce);
            Assert.Equal("read_only", routeRule.TargetAccess);

            var inputs = JsonDocument.Parse("""{"reviewModel":"o3-locked"}""").RootElement;
            var empty = JsonDocument.Parse("{}").RootElement;

            var before = _vm.Before(policy, "task.dispatch", "trk_1", empty, inputs, empty);
            var deny = Assert.IsType<GuardDecision.Deny>(before.Output);
            Assert.Equal("parent denied", deny.Reason);

            var after = _vm.After(policy, "task.settled", "trk_1", empty, inputs, empty);
            Assert.Equal("parent note", after.Output);

            var turn = _vm.PrepareTurn(
                policy,
                new PolicyTurnContext("trk_1", "ses_1", IsLead: true, BoundTaskId: null, ContextUsageRatio: 0.1, Inputs: inputs),
                empty);
            Assert.Equal("child context", turn.Output.Context);

            var dynamicRoute = _vm.RouteTaskDispatch(policy, "review", "read_only", null, "wrong-model", inputs);
            Assert.NotNull(dynamicRoute.Denial);
            Assert.Contains("o3-locked", dynamicRoute.Denial);
        }
    }

    [Fact]
    public void TrustedExtraction_RejectsSparseArraysAndSharedSubtreeExpansionWithinHostBudget()
    {
        const string sparseSource = """
            export default defineTemplate({
              id: "sparse-array",
              title: "Sparse Array",
              data: {
                schema: (t) => ({ values: t.array(t.string().optional()) }),
                initial: () => ({ values: new Array(1000000) }),
              },
            });
            """;

        var sparseEx = Assert.Throws<InvalidOperationException>(() => _vm.Compile(sparseSource));
        Assert.Contains("exceeds maximum", sparseEx.Message, StringComparison.OrdinalIgnoreCase);

        const string sharedSubtreeSource = """
            export default defineTemplate({
              id: "shared-dag",
              title: "Shared DAG",
              data: {
                schema: (t) => ({ tree: t.object({ a: t.array(t.string().optional()) }) }),
                initial: () => {
                  let node = { leaf: "x" };
                  for (let i = 0; i < 16; i += 1) {
                    node = { left: node, right: node };
                  }
                  return { tree: node };
                },
              },
            });
            """;

        var dagEx = Assert.Throws<InvalidOperationException>(() => _vm.Compile(sharedSubtreeSource));
        Assert.Contains("budget", dagEx.Message, StringComparison.OrdinalIgnoreCase);

        // Repeated forbidden keys in a patch must be bounded immediately instead of returning 20,000+ errors
        const string simpleSource = """
            export default defineTemplate({
              id: "bounded-errors",
              title: "Bounded Errors",
              data: {
                schema: (t) => ({ count: t.number() }),
                initial: () => ({ count: 1 }),
              },
            });
            """;
        var simple = _vm.Compile(simpleSource);
        var inputs = JsonDocument.Parse("{}").RootElement;
        var init = _vm.InitData(simple, inputs);
        var repeatedMetaJson = "{" + string.Join(",", Enumerable.Repeat("\"_meta\":0", 20_000)) + "}";
        var repeatedPatch = JsonDocument.Parse(repeatedMetaJson).RootElement;
        var boundedResult = _vm.ValidateAndPatchData(simple, inputs, init, repeatedPatch, [], "2026-11-26T00:00:00Z", "session:ai");
        Assert.False(boundedResult.Valid);
        Assert.True(boundedResult.Errors.Count <= 64, $"Expected bounded errors (<= 64), got {boundedResult.Errors.Count}");
    }

    [Fact]
    public void LargeAcceptedInitialData_SupportsScalarPatchAndIdempotentRetryWithinBudget()
    {
        const string source = """
            export default defineTemplate({
              id: "wide-initial",
              title: "Wide Initial",
              data: {
                initial: () => {
                  const flags = {};
                  for (let i = 0; i < 1800; i += 1) flags["k" + i] = true;
                  return { flags, note: "init" };
                },
              },
            });
            """;

        var compiled = _vm.Compile(source);
        var inputs = JsonDocument.Parse("{}").RootElement;
        var init = _vm.InitData(compiled, inputs);

        var updated = _vm.ValidateAndPatchData(
            compiled, inputs, init, JsonDocument.Parse("""{"note":"updated"}""").RootElement,
            [], "2026-11-26T00:00:01Z", "session:ai");
        Assert.True(updated.Valid);
        Assert.Equal("updated", updated.NextData.GetProperty("note").GetString());

        var noop = _vm.ValidateAndPatchData(
            compiled, inputs, updated.NextData, JsonDocument.Parse("""{"note":"updated"}""").RootElement,
            [], "2026-11-26T00:00:02Z", "session:ai");
        Assert.True(noop.Valid);
        Assert.True(JsonElement.DeepEquals(updated.NextData, noop.NextData));
    }

    [Fact]
    public void KeyedDeltaReplacementIdempotencyEmptyInitAndSchemaLessDocumentBudget()
    {
        const string keyedSource = """
            export default defineTemplate({
              id: "keyed-edge",
              title: "Keyed Edge",
              data: {
                schema: (t) => ({ records: t.array(t.object({ code: t.id(), title: t.string() })) }),
              },
            });
            """;
        var keyed = _vm.Compile(keyedSource);
        var inputs = JsonDocument.Parse("{}").RootElement;
        var empty = JsonDocument.Parse("{}").RootElement;

        // Empty keyed delta initializes missing array field.
        var initialized = _vm.ValidateAndPatchData(
            keyed, inputs, empty, JsonDocument.Parse("""{"records":{"$upsert":[]}}""").RootElement,
            [], "2026-11-26T00:00:00Z", "session:ai");
        Assert.True(initialized.Valid);
        Assert.Empty(initialized.NextData.GetProperty("records").EnumerateArray());

        // Delete + reinsert retry with same value and order is idempotent.
        var replacePatch = JsonDocument.Parse("""{"records":{"$delete":["a"],"$upsert":[{"code":"a","title":"v2"}]}}""").RootElement;
        var first = _vm.ValidateAndPatchData(keyed, inputs, initialized.NextData, replacePatch, [], "2026-11-26T00:00:01Z", "session:ai");
        Assert.True(first.Valid);
        var retry = _vm.ValidateAndPatchData(keyed, inputs, first.NextData, replacePatch, [], "2026-11-26T00:00:02Z", "session:ai");
        Assert.True(retry.Valid);
        Assert.True(JsonElement.DeepEquals(first.NextData, retry.NextData));

        // Schema-less tracks enforce the document-wide host node budget across patches.
        var schemaLess = _vm.Compile("export default defineTemplate({ id: 'free', title: 'Free' });");
        var state = JsonDocument.Parse("{}").RootElement;
        var exceeded = false;
        for (var batch = 0; batch < 4; batch++)
        {
            var props = string.Join(",", Enumerable.Range(batch * 2000, 2000).Select(i => $"\"k{i}\":{i}"));
            try
            {
                var res = _vm.ValidateAndPatchData(schemaLess, inputs, state, JsonDocument.Parse("{" + props + "}").RootElement,
                    [], "2026-11-26T00:00:03Z", "session:ai");
                state = res.NextData;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("node budget", StringComparison.OrdinalIgnoreCase))
            {
                exceeded = true;
                break;
            }
        }
        Assert.True(exceeded, "Expected schema-less document growth to hit the host node budget.");
    }
}
