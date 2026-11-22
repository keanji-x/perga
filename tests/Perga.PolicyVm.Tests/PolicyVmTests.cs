using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class PolicyVmTests
{
    private readonly PolicyVm _vm = new();

    private static string ResolveTemplatePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var slnCandidate = Path.Combine(dir.FullName, "Perga.sln");
            var tplCandidate = Path.Combine(dir.FullName, "templates", fileName);
            if (File.Exists(slnCandidate) && Directory.Exists(tplCandidate))
            {
                return tplCandidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate templates/{fileName} upward from {AppContext.BaseDirectory}.");
    }

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("agents-delivery", null)]
    [InlineData("equity-research", "weekly-price-refresh")]
    [InlineData("okr-loop", "daily-ci-check")]
    public void BuiltInTemplatesProjectFlowAndOnlySeedDeclaredClocks(string name, string? nodeId)
    {
        var policy = _vm.Compile(TemplateBundle.PackDirectory(ResolveTemplatePath(name)));
        Assert.Contains("builtin:flow", policy.Manifest.InitialReportMarkdown);
        var nodes = _vm.ResolveFlow(policy, ParseJson("{}"));
        if (nodeId is null)
        {
            Assert.Empty(nodes);
            Assert.True(policy.Manifest.RequireGateForReadWrite);
            var review = _vm.RouteTaskDispatch(policy, "review", "read_only", null, null, ParseJson("""{"reviewModel":"custom"}"""));
            Assert.Equal("custom", review.Model);
            return;
        }

        var clock = Assert.Single(nodes);
        Assert.Equal(nodeId, clock.Id);
        Assert.NotNull(clock.When?.Cron);
        Assert.Equal("wake", clock.Do.Kind);
        Assert.Equal("lead", clock.Do.Session);
        Assert.Contains("${inputs.", clock.Do.Prompt);
    }

    [Fact]
    public void HeaderViewUsesEffectiveInputsAndCurrentDataWithoutCountCapsOrWrites()
    {
        var policy = _vm.Compile("""
            export default defineTemplate({
              id: "view", title: "View", inputs: { unit: { type: "string", default: "steps" } },
              headerView(ctx) {
                const value = String(ctx.data.count) + " " + ctx.inputs.unit;
                ctx.data.count++;
                return {
                  badges: Array.from({length: 8}, () => ({label: ctx.trackId, tone: "accent"})),
                  notice: {text: "Ready", tone: "info"},
                  metrics: Array.from({length: 9}, () => ({label: "Count", value, delta: "+1", tone: "success", sub: "Today", spark: [1, 2, 3]})),
                  rules: [{label: "Rule", text: "Keep evidence"}]
                };
              }
            });
            """);
        var state = ParseJson("{\"count\":5}");
        var view = _vm.EvaluateHeaderView(policy, new("track", ParseJson("{}")), state);
        Assert.NotNull(view);
        Assert.Equal(8, view.Badges!.Count);
        Assert.All(view.Badges, badge => Assert.Equal("track", badge.Label));
        Assert.Equal(9, view.Metrics!.Count);
        Assert.Equal("5 steps", view.Metrics[0].Value);
        Assert.Equal(new double[] { 1, 2, 3 }, view.Metrics[0].Spark);
        Assert.Equal("Keep evidence", Assert.Single(view.Rules!).Text);
        Assert.Equal(5, state.GetProperty("count").GetInt32());
        Assert.Equal("5 steps", _vm.EvaluateHeaderView(policy, new("track", ParseJson("{}")), state)!.Metrics![0].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("headerView() { throw new Error('broken'); },")]
    [InlineData("headerView() { while (true) {} },")]
    [InlineData("headerView() { return null; },")]
    [InlineData("headerView() { return []; },")]
    [InlineData("headerView() { return 'wrong'; },")]
    [InlineData("headerView() { return {badges: 'wrong'}; },")]
    [InlineData("headerView() { return {badges: [null]}; },")]
    [InlineData("headerView() { return {badges: [{label: 'x', tone: 'purple'}]}; },")]
    [InlineData("headerView() { return {notice: {text: 'x', tone: 'success'}}; },")]
    [InlineData("headerView() { return {metrics: [{label: 'x', value: 5}]}; },")]
    [InlineData("headerView() { return {metrics: [{label: 'x', value: '5', spark: [null]}]}; },")]
    [InlineData("headerView() { return {rules: [{label: 'missing text'}]}; },")]
    [InlineData("headerView() { const v = {}; v.badges = v; return v; },")]
    public void HeaderViewFailsSoft(string hook)
    {
        var policy = _vm.Compile("export default defineTemplate({id: 'view', title: 'View', " + hook + "});");
        Assert.Null(_vm.EvaluateHeaderView(policy, new("track", ParseJson("{}")), ParseJson("{}")));
    }

    [Fact]
    public void BeforeReport_UsesPayloadAndDataWithoutPersistingMutations()
    {
        var policy = _vm.Compile("""
            export default {id:'report',title:'Report',before(a,ctx) {
              if(a.type !== 'report.commit') return Allow();
              ctx.data.validations=(ctx.data.validations||0)+1;
              if(a.nextMarkdown.includes(ctx.data.forbidden)) return Deny('Contains forbidden marker.');
              if(a.nextMarkdown.includes('SIGN_OFF')) return AskHuman('Approve edit?',{approve:'Approve',deny:'Reject'});
              return Allow();
            }};
            """);
        var inputs = ParseJson("{}");
        var data = ParseJson("""{"forbidden":"FORBIDDEN"}""");
        var denied = _vm.Before(policy, "report.commit", "track", ParseJson("""{"nextMarkdown":"FORBIDDEN"}"""), inputs, data);
        var ask = _vm.Before(policy, "report.commit", "track", ParseJson("""{"nextMarkdown":"SIGN_OFF"}"""), inputs, data);
        Assert.IsType<GuardDecision.Deny>(denied);
        Assert.IsType<GuardDecision.AskHuman>(ask);
        Assert.False(data.TryGetProperty("validations", out _));
    }

    [Fact]
    public void Sandbox_RejectsInfiniteLoop_StatementOverflow_MemoryBomb_DeepRecursion_StrictViolations_AndHostAccess()
    {
        // 1. Infinite loop / statement budget overflow
        var loopPolicy = _vm.Compile("""
            export default defineTemplate({
              id: "loop",
              title: "Loop",
              before: () => {
                while (true) {}
              }
            });
            """);

        var loopEx = Assert.Throws<InvalidOperationException>(() =>
            _vm.Before(loopPolicy, "tool.task_list", "t1",
                JsonSerializer.SerializeToElement(new {
                    sessionId = "s1",
                    isLead = true,
                    boundTaskId = (object?)null,
                    mcpServer = "perga",
                    toolName = "task_list",
                    arguments = ParseJson("{}")
                }), ParseJson("{}"), ParseJson("{}")));
        Assert.Contains("sandbox", loopEx.Message, StringComparison.OrdinalIgnoreCase);

        // 2. Statement budget (> 10,000 statements) rejected during Compile
        Assert.Throws<InvalidOperationException>(() =>
            _vm.Compile("""
                let sum = 0;
                for (let i = 0; i < 25000; i++) {
                  sum += i;
                }
                export default defineTemplate({ id: "stmt", title: String(sum) });
                """));

        // 3. Memory bomb (> 16 MB) rejected
        var memoryPolicy = _vm.Compile("""
            export default defineTemplate({
              id: "mem",
              title: "Mem",
              before: () => {
                const chunks = [];
                const unit = "x".repeat(1024 * 512);
                for (let i = 0; i < 64; i++) {
                  chunks.push(unit + i);
                }
                return Allow();
              }
            });
            """);
        var memEx = Assert.Throws<InvalidOperationException>(() =>
            _vm.Before(memoryPolicy, "tool.task_list", "t1", ParseJson("{}"), ParseJson("{}"), ParseJson("{}")));
        Assert.Contains("memory", memEx.Message, StringComparison.OrdinalIgnoreCase);

        // 4. Recursion depth (> 64) rejected
        var recursionPolicy = _vm.Compile("""
            function recurse(n) {
              return n <= 0 ? 0 : 1 + recurse(n - 1);
            }
            export default defineTemplate({
              id: "rec",
              title: "Recursion",
              before: () => Deny(String(recurse(200)))
            });
            """);
        var recEx = Assert.Throws<InvalidOperationException>(() =>
            _vm.Before(recursionPolicy, "tool.task_list", "t1", ParseJson("{}"), ParseJson("{}"), ParseJson("{}")));
        Assert.Contains("recursion", recEx.Message, StringComparison.OrdinalIgnoreCase);

        // 5. Strict mode enforced (implicit global variable throws ReferenceError)
        Assert.Throws<InvalidOperationException>(() =>
            _vm.Compile("""
                undeclaredGlobal = 42;
                export default defineTemplate({ id: "strict-fail", title: "Strict" });
                """));

        // 6. Zero CLR reflection, process, require, fetch, or external module imports
        var probePolicy = _vm.Compile("""
            export default defineTemplate({
              id: "probe",
              title: "Host Isolation Probe",
              data: { initial: () => ({
                hasSystem: typeof globalThis.System !== "undefined",
                hasImportNamespace: typeof globalThis.importNamespace !== "undefined",
                hasProcess: typeof globalThis.process !== "undefined",
                hasRequire: typeof globalThis.require !== "undefined",
                hasFetch: typeof globalThis.fetch !== "undefined"
              }) }
            });
            """);
        var probeData = _vm.InitData(probePolicy, ParseJson("{}"));
        Assert.False(probeData.GetProperty("hasSystem").GetBoolean());
        Assert.False(probeData.GetProperty("hasImportNamespace").GetBoolean());
        Assert.False(probeData.GetProperty("hasProcess").GetBoolean());
        Assert.False(probeData.GetProperty("hasRequire").GetBoolean());
        Assert.False(probeData.GetProperty("hasFetch").GetBoolean());

        // External filesystem / module import blocked (both bare and relative paths)
        Assert.ThrowsAny<Exception>(() =>
            _vm.Compile("""
                import fs from "fs";
                export default defineTemplate({ id: "fs-escape", title: String(fs) });
                """));

        Assert.ThrowsAny<Exception>(() =>
            _vm.Compile("""
                import dev from "./unbundled-module.js";
                export default defineTemplate({ id: "rel-escape", title: String(dev) });
                """));
    }

    [Fact]
    public void GlobalHelpersReadDefaultInputs()
    {
        var compiled = _vm.Compile("""
            export default defineTemplate({
              id: "helpers",
              title: "Helpers",
              inputs: {
                envName: { type: "string", default: "staging" }
              },
              before: (call, ctx) => {
                if (call.toolName === "ask_no_opts") {
                  return AskHuman(`Confirm on ${ctx.inputs.envName}?`);
                }
                if (call.toolName === "deny_tool") {
                  return Deny(`Blocked on ${ctx.inputs.envName}`);
                }
                return Allow();
              }
            });
            """);

        var emptyInputs = ParseJson("{}");
        var data = ParseJson("{}");

        var askNoOpts = _vm.Before(compiled, "tool.ask_no_opts", "t1",
            JsonSerializer.SerializeToElement(new {
                sessionId = "s1",
                isLead = true,
                boundTaskId = (object?)null,
                mcpServer = "perga",
                toolName = "ask_no_opts",
                arguments = emptyInputs
            }), emptyInputs, data);
        var ask = Assert.IsType<GuardDecision.AskHuman>(askNoOpts);
        Assert.Equal("Confirm on staging?", ask.Title);
        Assert.Equal("Approve", ask.ApproveLabel);
        Assert.Equal("Deny", ask.DenyLabel);

        var denyRes = _vm.Before(compiled, "tool.deny_tool", "t1",
            JsonSerializer.SerializeToElement(new {
                sessionId = "s1",
                isLead = true,
                boundTaskId = (object?)null,
                mcpServer = "perga",
                toolName = "deny_tool",
                arguments = emptyInputs
            }), emptyInputs, data);
        var deny = Assert.IsType<GuardDecision.Deny>(denyRes);
        Assert.Equal("Blocked on staging", deny.Reason);

    }
}
