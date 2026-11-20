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
            if (File.Exists(slnCandidate) && File.Exists(tplCandidate))
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

    private PolicyEvalResult<string?> FireSchedule(CompiledPolicy policy, string id, JsonElement inputs, JsonElement state)
    {
        var spec = policy.Manifest.Schedules.Single(s => s.Id == id);
        var prompt = _vm.InterpolateInputs(policy, spec.Prompt, inputs);
        return _vm.After(policy, "schedule.fired", "track", JsonSerializer.SerializeToElement(new { scheduleId = id, sourceKind = "template", prompt }), inputs, state);
    }

    [Theory]
    [InlineData("dev.perga.js")]
    [InlineData("investment-research.perga.js")]
    [InlineData("long-term-goal.perga.js")]
    public void BuiltInTemplatesStartEmptyWithUsefulHeaders(string name)
    {
        var policy = _vm.Compile(File.ReadAllText(ResolveTemplatePath(name)));
        var inputs = ParseJson("{}");
        var state = _vm.InitState(policy, inputs).Output;
        var view = _vm.EvaluateHeaderView(policy, new("track", inputs), state);
        Assert.NotNull(view);
        Assert.NotEmpty(view.Badges!);
        Assert.NotEmpty(view.Metrics!);
        Assert.NotEmpty(view.Rules!);
        Assert.NotNull(view.Notice);
    }

    [Fact]
    public void HeaderViewUsesEffectiveInputsAndCurrentStateWithoutCountCapsOrStateWrites()
    {
        var policy = _vm.Compile("""
            export default defineTemplate({
              id: "view", title: "View", inputs: { unit: { type: "string", default: "steps" } },
              headerView(ctx) {
                const value = String(ctx.state.count) + " " + ctx.inputs.unit;
                ctx.state.count++;
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
    public void BuiltInDevTemplate_CompilesAndExecutesFullLifecycleAcrossHooks()
    {
        var source = File.ReadAllText(ResolveTemplatePath("dev.perga.js"));
        var compiled = _vm.Compile(source);

        Assert.Equal("dev", compiled.TemplateId);
        Assert.Equal("dev", compiled.Manifest.Id);
        Assert.Equal("vcs-workspace", compiled.Manifest.WorkspaceDriver);
        Assert.True(compiled.Manifest.RequireGateForReadWrite);
        Assert.Equal(24000, compiled.Manifest.MaxProseChars);
        Assert.Contains("node_modules", compiled.Manifest.WorkspaceLowerCacheDirs);
        Assert.NotEmpty(compiled.Manifest.AfterWorkerDoneCmd!);
        Assert.Single(compiled.Manifest.Schedules);
        Assert.NotEmpty(compiled.Manifest.Schedules[0].Prompt);

        var inputs = ParseJson("""{"baseBranch":"release/1.0","riskLevel":"high"}""");
        var init = _vm.InitState(compiled, inputs);
        var state = init.NextState;
        Assert.Equal(0, state.GetProperty("iteration").GetInt32());
        Assert.False(state.GetProperty("ciVerified").GetBoolean());

        // Turn context includes role, upstream branch, and handover notice at high context usage
        var turnRes = _vm.PrepareTurn(
            compiled,
            new PolicyTurnContext("trk_dev", "ses_lead", IsLead: true, BoundAttemptId: null, ContextUsageRatio: 0.82, Inputs: inputs),
            state);
        Assert.Contains("release/1.0", turnRes.Output.Context);
        Assert.Contains("Epoch handover", turnRes.Output.Context);

        // Before tool: vcs_pr_merge is denied before read_write gate verification
        var prematureMerge = _vm.Before(compiled, "tool.vcs_pr_merge", "trk_dev",
            JsonSerializer.SerializeToElement(new {
                sessionId = "ses_lead",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "vcs",
                toolName = "vcs_pr_merge",
                arguments = ParseJson("""{"prNumber":12}""")
            }), inputs, state);
        var denyMerge = Assert.IsType<GuardDecision.Deny>(prematureMerge.Output);
        Assert.Contains("ciVerified=false", denyMerge.Reason);

        // After task.settled: read_write task passes gate and records JJ changeId + opId
        var settled = _vm.After(compiled, "task.settled", "trk_dev",
            JsonSerializer.SerializeToElement(new {
                taskKey = "impl-core",
                accessMode = "read_write",
                attemptStatus = "done",
                workerResult = ParseJson("""{"summary":"Implemented engine"}"""),
                meta = ParseJson("""{"changeId":"kxyz1234","opId":"op_9988"}"""),
                gateVerdict = ParseJson("""{"passed":true}""")
            }), inputs, state);
        state = settled.NextState;

        Assert.Equal(1, state.GetProperty("iteration").GetInt32());
        Assert.Equal("kxyz1234", state.GetProperty("latestChangeId").GetString());
        Assert.Equal("op_9988", state.GetProperty("rollbackOpId").GetString());
        Assert.True(state.GetProperty("ciVerified").GetBoolean());
        Assert.Contains("impl-core", settled.Output);

        // Before tool: high-risk vcs_publish_pr triggers AskHuman and mutates prPublished=true
        var publishRes = _vm.Before(compiled, "tool.vcs_publish_pr", "trk_dev",
            JsonSerializer.SerializeToElement(new {
                sessionId = "ses_lead",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "vcs",
                toolName = "vcs_publish_pr",
                arguments = ParseJson("{}")
            }), inputs, state);
        state = publishRes.NextState;
        var askPublish = Assert.IsType<GuardDecision.AskHuman>(publishRes.Output);
        Assert.Contains("release/1.0", askPublish.Title);
        Assert.True(state.GetProperty("prPublished").GetBoolean());

        // Before tool: now that ciVerified=true and requirePrReview=true (default), vcs_pr_merge returns AskHuman
        var mergeAsk = _vm.Before(compiled, "tool.vcs_pr_merge", "trk_dev",
            JsonSerializer.SerializeToElement(new {
                sessionId = "ses_lead",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "vcs",
                toolName = "vcs_pr_merge",
                arguments = ParseJson("""{"prNumber":12}""")
            }), inputs, state);
        var askDecision = Assert.IsType<GuardDecision.AskHuman>(mergeAsk.Output);
        Assert.Equal("Approve & Merge", askDecision.ApproveLabel);
        Assert.Equal("Hold for Manual Review", askDecision.DenyLabel);

        // Dynamic schedule prompt
        var wakeRes = FireSchedule(compiled, "upstream-drift-check", inputs, state);
        Assert.Contains("release/1.0", wakeRes.Output);
        Assert.Contains("completed tasks: 1", wakeRes.Output);
    }

    [Fact]
    public void BuiltInLongTermGoalTemplate_HandlesSchedulesCircuitBreakerAndTrackCloseApproval()
    {
        var source = File.ReadAllText(ResolveTemplatePath("long-term-goal.perga.js"));
        var compiled = _vm.Compile(source);

        Assert.Equal("long-term-goal", compiled.TemplateId);
        Assert.Equal("direct", compiled.Manifest.WorkspaceDriver);
        Assert.Equal(2, compiled.Manifest.Schedules.Count);
        Assert.NotEmpty(compiled.Manifest.Schedules[0].Prompt);
        Assert.NotNull(compiled.Manifest.Schedules[1].Prompt);

        var inputs = ParseJson("""{"cadence":"daily","targetMetric":"Ship v1.0"}""");
        var state = _vm.InitState(compiled, inputs).NextState;

        // Dynamic schedule mutates checkInCount in state
        var checkIn1 = FireSchedule(compiled, "cadence-checkin", inputs, state);
        state = checkIn1.NextState;
        Assert.Equal(1, state.GetProperty("checkInCount").GetInt32());
        Assert.Contains("daily check-in", checkIn1.Output);
        Assert.Contains("Check-in #1", checkIn1.Output);
        Assert.Contains("Ship v1.0", checkIn1.Output);

        // Static schedule returns static prompt without mutating state
        var retro = FireSchedule(compiled, "friday-retrospective", inputs, state);
        Assert.Equal(compiled.Manifest.Schedules[1].Prompt, retro.Output);

        // 3 consecutive failures trip pausedForHuman circuit breaker
        for (var i = 1; i <= 3; i++)
        {
            var failRes = _vm.After(compiled, "task.settled", "trk_ltg",
                JsonSerializer.SerializeToElement(new {
                    taskKey = $"milestone-{i}",
                    accessMode = "read_write",
                    attemptStatus = "failed",
                    workerResult = (object?)null,
                    meta = ParseJson("{}"),
                    gateVerdict = (object?)null
                }), inputs, state);
            state = failRes.NextState;
        }

        Assert.True(state.GetProperty("pausedForHuman").GetBoolean());

        // Cross-track mail blocked while pausedForHuman is active
        var mailGuard = _vm.Before(compiled, "tool.perga_mail_send", "trk_ltg",
            JsonSerializer.SerializeToElement(new {
                sessionId = "ses_1",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "perga",
                toolName = "perga_mail_send",
                arguments = ParseJson("{}")
            }), inputs, state);
        Assert.IsType<GuardDecision.Deny>(mailGuard.Output);

        // Closing track asks human confirmation
        var closeGuard = _vm.Before(compiled, "tool.perga_track_close", "trk_ltg",
            JsonSerializer.SerializeToElement(new {
                sessionId = "ses_1",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "perga",
                toolName = "perga_track_close",
                arguments = ParseJson("{}")
            }), inputs, state);
        Assert.IsType<GuardDecision.AskHuman>(closeGuard.Output);
    }

    [Fact]
    public void BuiltInInvestmentResearchTemplate_EnforcesPluginsPaperTradingAndReportValidation()
    {
        var source = File.ReadAllText(ResolveTemplatePath("investment-research.perga.js"));
        var compiled = _vm.Compile(source);

        Assert.Equal("investment-research", compiled.TemplateId);
        Assert.Equal(
            ["market", "barra", "invest", "paper-trading", "research-archive"],
            compiled.Manifest.DefaultPlugins);

        var disabledOrdersInputs = ParseJson("""{"tickerOrTheme":"600519.SH","allowPaperOrders":false}""");
        var state = _vm.InitState(compiled, disabledOrdersInputs).NextState;

        // Read-only paper-trading positions query allowed even when allowPaperOrders=false
        var readPos = _vm.Before(compiled, "tool.portfolio_positions", "trk_inv",
            JsonSerializer.SerializeToElement(new {
                sessionId = "ses_1",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "paper-trading",
                toolName = "portfolio_positions",
                arguments = ParseJson("{}")
            }), disabledOrdersInputs, state);
        Assert.IsType<GuardDecision.Allow>(readPos.Output);

        // Order placement denied when allowPaperOrders=false
        var deniedOrder = _vm.Before(compiled, "tool.submit_order", "trk_inv",
            JsonSerializer.SerializeToElement(new {
                sessionId = "ses_1",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "paper-trading",
                toolName = "submit_order",
                arguments = ParseJson("""{"qty":100}""")
            }), disabledOrdersInputs, state);
        Assert.IsType<GuardDecision.Deny>(deniedOrder.Output);

        // Enable allowPaperOrders=true, settle a research task with verifiedSources + convictionScore
        var enabledInputs = ParseJson("""{"tickerOrTheme":"600519.SH","allowPaperOrders":true}""");
        var settled = _vm.After(compiled, "task.settled", "trk_inv",
            JsonSerializer.SerializeToElement(new {
                taskKey = "dcf-model",
                accessMode = "read_only",
                attemptStatus = "done",
                workerResult = ParseJson("""{"verifiedSources":4,"convictionScore":82}"""),
                meta = ParseJson("{}"),
                gateVerdict = (object?)null
            }), enabledInputs, state);
        state = settled.NextState;
        Assert.Equal(4, state.GetProperty("dataSourcesVerified").GetInt32());
        Assert.Equal(82, state.GetProperty("convictionScore").GetInt32());

        // Order placement now asks human approval and increments paperOrdersSubmitted
        var askOrder = _vm.Before(compiled, "tool.submit_order", "trk_inv",
            JsonSerializer.SerializeToElement(new {
                sessionId = "ses_1",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "paper-trading",
                toolName = "submit_order",
                arguments = ParseJson("""{"qty":100}""")
            }), enabledInputs, state);
        Assert.IsType<GuardDecision.AskHuman>(askOrder.Output);
        Assert.Equal(1, askOrder.NextState.GetProperty("paperOrdersSubmitted").GetInt32());

        // Before report.commit blocks TODO:UNVERIFIED in Executive Thesis
        var invalidThesis = _vm.Before(compiled, "report.commit", "trk_inv",
            JsonSerializer.SerializeToElement(new {
                editorRole = "lead",
                prevMarkdown = "# Executive Thesis\n\nInitial",
                nextMarkdown = "# Executive Thesis\n\nTarget EV/EBITDA: TODO:UNVERIFIED",
                touchedSections = new string[] {"Executive Thesis"},
                nextProseChars = 60
            }), enabledInputs, state);
        Assert.IsType<GuardDecision.Deny>(invalidThesis.Output);
    }

    [Fact]
    public void BeforeReport_CanDenyAskAndUpdateState()
    {
        var policy = _vm.Compile("""
            export default {id:'report',title:'Report',before(a,ctx) {
              if(a.type !== 'report.commit') return Allow();
              ctx.state.validations=(ctx.state.validations||0)+1;
              if(a.nextMarkdown.includes('FORBIDDEN')) return Deny('Contains forbidden marker.');
              if(a.nextMarkdown.includes('SIGN_OFF')) return AskHuman('Approve edit?',{approve:'Approve',deny:'Reject'});
              return Allow();
            }};
            """);
        var result = _vm.Before(policy,"report.commit","track",ParseJson("""{"nextMarkdown":"FORBIDDEN"}"""),ParseJson("{}"),ParseJson("{}"));
        Assert.IsType<GuardDecision.Deny>(result.Output);
        Assert.Equal(1,result.NextState.GetProperty("validations").GetInt32());
        var ask = _vm.Before(policy,"report.commit","track",ParseJson("""{"nextMarkdown":"SIGN_OFF"}"""),ParseJson("{}"),result.NextState);
        Assert.IsType<GuardDecision.AskHuman>(ask.Output);
        Assert.Equal(2,ask.NextState.GetProperty("validations").GetInt32());
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
            _vm.Before(loopPolicy, "tool.perga_task_ls", "t1",
                JsonSerializer.SerializeToElement(new {
                    sessionId = "s1",
                    isLead = true,
                    boundAttemptId = (object?)null,
                    mcpServer = "perga",
                    toolName = "perga_task_ls",
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
              initialState: () => {
                const chunks = [];
                const unit = "x".repeat(1024 * 512);
                for (let i = 0; i < 64; i++) {
                  chunks.push(unit + i);
                }
                return { count: chunks.length };
              }
            });
            """);
        var memEx = Assert.Throws<InvalidOperationException>(() =>
            _vm.InitState(memoryPolicy, ParseJson("{}")));
        Assert.Contains("memory", memEx.Message, StringComparison.OrdinalIgnoreCase);

        // 4. Recursion depth (> 64) rejected
        var recursionPolicy = _vm.Compile("""
            function recurse(n) {
              return n <= 0 ? 0 : 1 + recurse(n - 1);
            }
            export default defineTemplate({
              id: "rec",
              title: "Recursion",
              initialState: () => ({ depth: recurse(200) })
            });
            """);
        var recEx = Assert.Throws<InvalidOperationException>(() =>
            _vm.InitState(recursionPolicy, ParseJson("{}")));
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
              initialState: () => ({
                hasSystem: typeof globalThis.System !== "undefined",
                hasImportNamespace: typeof globalThis.importNamespace !== "undefined",
                hasProcess: typeof globalThis.process !== "undefined",
                hasRequire: typeof globalThis.require !== "undefined",
                hasFetch: typeof globalThis.fetch !== "undefined"
              })
            });
            """);
        var probeState = _vm.InitState(probePolicy, ParseJson("{}")).NextState;
        Assert.False(probeState.GetProperty("hasSystem").GetBoolean());
        Assert.False(probeState.GetProperty("hasImportNamespace").GetBoolean());
        Assert.False(probeState.GetProperty("hasProcess").GetBoolean());
        Assert.False(probeState.GetProperty("hasRequire").GetBoolean());
        Assert.False(probeState.GetProperty("hasFetch").GetBoolean());

        // External filesystem / module import blocked (both bare and relative paths)
        Assert.ThrowsAny<Exception>(() =>
            _vm.Compile("""
                import fs from "fs";
                export default defineTemplate({ id: "fs-escape", title: String(fs) });
                """));

        Assert.ThrowsAny<Exception>(() =>
            _vm.Compile("""
                import dev from "./dev.perga.js";
                export default defineTemplate({ id: "rel-escape", title: String(dev) });
                """));
    }

    [Fact]
    public void GlobalHelpers_DefaultInputs_AndUnknownScheduleId_BehaveDeterministically()
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
              },
              schedules: [
                { id: "known", cron: "0 * * * *", prompt: "Ping" }
              ]
            });
            """);

        var emptyInputs = ParseJson("{}");
        var state = _vm.InitState(compiled, emptyInputs).NextState;

        var askNoOpts = _vm.Before(compiled, "tool.ask_no_opts", "t1",
            JsonSerializer.SerializeToElement(new {
                sessionId = "s1",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "perga",
                toolName = "ask_no_opts",
                arguments = emptyInputs
            }), emptyInputs, state);
        var ask = Assert.IsType<GuardDecision.AskHuman>(askNoOpts.Output);
        Assert.Equal("Confirm on staging?", ask.Title);
        Assert.Equal("Approve", ask.ApproveLabel);
        Assert.Equal("Deny", ask.DenyLabel);

        var denyRes = _vm.Before(compiled, "tool.deny_tool", "t1",
            JsonSerializer.SerializeToElement(new {
                sessionId = "s1",
                isLead = true,
                boundAttemptId = (object?)null,
                mcpServer = "perga",
                toolName = "deny_tool",
                arguments = emptyInputs
            }), emptyInputs, state);
        var deny = Assert.IsType<GuardDecision.Deny>(denyRes.Output);
        Assert.Equal("Blocked on staging", deny.Reason);

        var unknownSched = _vm.After(compiled, "schedule.fired", "track", ParseJson("{}"), emptyInputs, state);
        Assert.Null(unknownSched.Output);
    }
}
