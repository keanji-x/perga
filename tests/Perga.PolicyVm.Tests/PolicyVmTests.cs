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
        Assert.Null(compiled.Manifest.Schedules[0].StaticWakePrompt); // Dynamic function wakePrompt

        var inputs = ParseJson("""{"baseBranch":"release/1.0","riskLevel":"high"}""");
        var init = _vm.InitState(compiled, inputs);
        var state = init.NextState;
        Assert.Equal(0, state.GetProperty("iteration").GetInt32());
        Assert.False(state.GetProperty("ciVerified").GetBoolean());

        // Turn context includes role, upstream branch, and handover notice at high context usage
        var turnRes = _vm.BuildTurnContext(
            compiled,
            new PolicyTurnContext("trk_dev", "ses_lead", IsLead: true, BoundAttemptId: null, ContextUsageRatio: 0.82, Inputs: inputs),
            state);
        Assert.Contains("release/1.0", turnRes.Output);
        Assert.Contains("Epoch handover", turnRes.Output);

        // GuardToolCall: vcs_pr_merge is denied before read_write gate verification
        var prematureMerge = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("trk_dev", "ses_lead", true, null, "vcs", "vcs_pr_merge", ParseJson("""{"prNumber":12}"""), inputs),
            state);
        var denyMerge = Assert.IsType<GuardDecision.Deny>(prematureMerge.Output);
        Assert.Contains("ciVerified=false", denyMerge.Reason);

        // OnTaskSettled: read_write task passes gate and records JJ changeId + opId
        var settled = _vm.OnTaskSettled(
            compiled,
            new PolicyTaskSettledContext(
                TrackId: "trk_dev",
                TaskKey: "impl-core",
                AccessMode: "read_write",
                AttemptStatus: "done",
                WorkerResult: ParseJson("""{"summary":"Implemented engine"}"""),
                AttemptMeta: ParseJson("""{"changeId":"kxyz1234","opId":"op_9988"}"""),
                GateVerdict: ParseJson("""{"passed":true}"""),
                Inputs: inputs),
            state);
        state = settled.NextState;

        Assert.Equal(1, state.GetProperty("iteration").GetInt32());
        Assert.Equal("kxyz1234", state.GetProperty("latestChangeId").GetString());
        Assert.Equal("op_9988", state.GetProperty("rollbackOpId").GetString());
        Assert.True(state.GetProperty("ciVerified").GetBoolean());
        Assert.Contains("impl-core", settled.Output);

        // GuardToolCall: high-risk vcs_publish_pr triggers AskHuman and mutates prPublished=true
        var publishRes = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("trk_dev", "ses_lead", true, null, "vcs", "vcs_publish_pr", ParseJson("{}"), inputs),
            state);
        state = publishRes.NextState;
        var askPublish = Assert.IsType<GuardDecision.AskHuman>(publishRes.Output);
        Assert.Contains("release/1.0", askPublish.Title);
        Assert.True(state.GetProperty("prPublished").GetBoolean());

        // GuardToolCall: now that ciVerified=true and requirePrReview=true (default), vcs_pr_merge returns AskHuman
        var mergeAsk = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("trk_dev", "ses_lead", true, null, "vcs", "vcs_pr_merge", ParseJson("""{"prNumber":12}"""), inputs),
            state);
        var askDecision = Assert.IsType<GuardDecision.AskHuman>(mergeAsk.Output);
        Assert.Equal(2, askDecision.Options.Count);

        // Dynamic schedule prompt
        var wakeRes = _vm.BuildScheduleWakePrompt(compiled, "upstream-drift-check", inputs, state);
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
        Assert.Null(compiled.Manifest.Schedules[0].StaticWakePrompt);
        Assert.NotNull(compiled.Manifest.Schedules[1].StaticWakePrompt);

        var inputs = ParseJson("""{"cadence":"daily","targetMetric":"Ship v1.0"}""");
        var state = _vm.InitState(compiled, inputs).NextState;

        // Dynamic schedule mutates checkInCount in state
        var checkIn1 = _vm.BuildScheduleWakePrompt(compiled, "cadence-checkin", inputs, state);
        state = checkIn1.NextState;
        Assert.Equal(1, state.GetProperty("checkInCount").GetInt32());
        Assert.Contains("daily check-in #1", checkIn1.Output);
        Assert.Contains("Ship v1.0", checkIn1.Output);

        // Static schedule returns static prompt without mutating state
        var retro = _vm.BuildScheduleWakePrompt(compiled, "friday-retrospective", inputs, state);
        Assert.Equal(compiled.Manifest.Schedules[1].StaticWakePrompt, retro.Output);

        // 3 consecutive failures trip pausedForHuman circuit breaker
        for (var i = 1; i <= 3; i++)
        {
            var failRes = _vm.OnTaskSettled(
                compiled,
                new PolicyTaskSettledContext(
                    "trk_ltg",
                    $"milestone-{i}",
                    "read_write",
                    "failed",
                    null,
                    ParseJson("{}"),
                    null,
                    inputs),
                state);
            state = failRes.NextState;
        }

        Assert.True(state.GetProperty("pausedForHuman").GetBoolean());

        // Cross-track mail blocked while pausedForHuman is active
        var mailGuard = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("trk_ltg", "ses_1", true, null, "perga", "perga_mail_send", ParseJson("{}"), inputs),
            state);
        Assert.IsType<GuardDecision.Deny>(mailGuard.Output);

        // Closing track asks human confirmation
        var closeGuard = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("trk_ltg", "ses_1", true, null, "perga", "perga_track_close", ParseJson("{}"), inputs),
            state);
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
        var readPos = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("trk_inv", "ses_1", true, null, "paper-trading", "portfolio_positions", ParseJson("{}"), disabledOrdersInputs),
            state);
        Assert.IsType<GuardDecision.Allow>(readPos.Output);

        // Order placement denied when allowPaperOrders=false
        var deniedOrder = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("trk_inv", "ses_1", true, null, "paper-trading", "submit_order", ParseJson("""{"qty":100}"""), disabledOrdersInputs),
            state);
        Assert.IsType<GuardDecision.Deny>(deniedOrder.Output);

        // Enable allowPaperOrders=true, settle a research task with verifiedSources + convictionScore
        var enabledInputs = ParseJson("""{"tickerOrTheme":"600519.SH","allowPaperOrders":true}""");
        var settled = _vm.OnTaskSettled(
            compiled,
            new PolicyTaskSettledContext(
                "trk_inv",
                "dcf-model",
                "read_only",
                "done",
                ParseJson("""{"verifiedSources":4,"convictionScore":82}"""),
                ParseJson("{}"),
                null,
                enabledInputs),
            state);
        state = settled.NextState;
        Assert.Equal(4, state.GetProperty("dataSourcesVerified").GetInt32());
        Assert.Equal(82, state.GetProperty("convictionScore").GetInt32());

        // Order placement now asks human approval and increments paperOrdersSubmitted
        var askOrder = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("trk_inv", "ses_1", true, null, "paper-trading", "submit_order", ParseJson("""{"qty":100}"""), enabledInputs),
            state);
        Assert.IsType<GuardDecision.AskHuman>(askOrder.Output);
        Assert.Equal(1, askOrder.NextState.GetProperty("paperOrdersSubmitted").GetInt32());

        // Report customValidate blocks TODO:UNVERIFIED in Executive Thesis
        var invalidThesis = _vm.GuardReportCommit(
            compiled,
            new PolicyReportCommitContext(
                "trk_inv",
                "lead",
                "# Executive Thesis\n\nInitial",
                "# Executive Thesis\n\nTarget EV/EBITDA: TODO:UNVERIFIED",
                ["Executive Thesis"],
                60,
                enabledInputs),
            state);
        Assert.IsType<GuardDecision.Deny>(invalidThesis.Output);
    }

    [Fact]
    public void GuardReportCommit_EnforcesMaxProseChars_CaseInsensitiveEditableBy_AndStateMutatingCustomValidate()
    {
        const string policyJs = """
            export default defineTemplate({
              id: "report-guard-test",
              title: "Report Guard Test",
              initialState: () => ({ validations: 0 }),
              report: {
                maxProseChars: 100,
                sections: [
                  { h1: "Executive Summary", editableBy: ["human", "lead"] },
                  { h1: "Worker Notes", editableBy: ["human", "lead", "subagent"] }
                ],
                customValidate: (ctx, info) => {
                  ctx.state.validations += 1;
                  if (info.nextMarkdown.includes("FORBIDDEN")) {
                    return Deny("Contains forbidden marker.");
                  }
                  if (info.nextMarkdown.includes("NEEDS_SIGN_OFF")) {
                    return AskHuman("Approve sensitive report edit?", ["Approve", "Reject"]);
                  }
                  return Allow();
                }
              }
            });
            """;

        var compiled = _vm.Compile(policyJs);
        var emptyInputs = ParseJson("{}");
        var state = _vm.InitState(compiled, emptyInputs).NextState;

        // 1. Exceeding MaxProseChars denied
        var overLimit = _vm.GuardReportCommit(
            compiled,
            new PolicyReportCommitContext("t1", "lead", "", "long", ["Executive Summary"], NextProseChars: 101, emptyInputs),
            state);
        Assert.IsType<GuardDecision.Deny>(overLimit.Output);

        // 2. EditableBy enforced case-insensitively (SUBAGENT rejected on Executive Summary, allowed on Worker Notes)
        var unauthorizedRole = _vm.GuardReportCommit(
            compiled,
            new PolicyReportCommitContext("t1", "SUBAGENT", "", "ok", ["# executive summary"], NextProseChars: 20, emptyInputs),
            state);
        var roleDeny = Assert.IsType<GuardDecision.Deny>(unauthorizedRole.Output);
        Assert.Contains("Executive Summary", roleDeny.Reason);

        var authorizedUpperLead = _vm.GuardReportCommit(
            compiled,
            new PolicyReportCommitContext("t1", "LEAD", "", "Clean update", ["EXECUTIVE SUMMARY"], NextProseChars: 20, emptyInputs),
            state);
        Assert.IsType<GuardDecision.Allow>(authorizedUpperLead.Output);
        Assert.Equal(1, authorizedUpperLead.NextState.GetProperty("validations").GetInt32());

        // 3. customValidate AskHuman + state propagation
        var signOffRes = _vm.GuardReportCommit(
            compiled,
            new PolicyReportCommitContext("t1", "subagent", "", "NEEDS_SIGN_OFF", ["Worker Notes"], NextProseChars: 20, emptyInputs),
            authorizedUpperLead.NextState);
        var ask = Assert.IsType<GuardDecision.AskHuman>(signOffRes.Output);
        Assert.Equal("Approve sensitive report edit?", ask.Title);
        Assert.Equal(["Approve", "Reject"], ask.Options);
        Assert.Equal(2, signOffRes.NextState.GetProperty("validations").GetInt32());
    }

    [Fact]
    public void Sandbox_RejectsInfiniteLoop_StatementOverflow_MemoryBomb_DeepRecursion_StrictViolations_AndHostAccess()
    {
        // 1. Infinite loop / statement budget overflow
        var loopPolicy = _vm.Compile("""
            export default defineTemplate({
              id: "loop",
              title: "Loop",
              guardToolCall: () => {
                while (true) {}
              }
            });
            """);

        var loopEx = Assert.Throws<InvalidOperationException>(() =>
            _vm.GuardToolCall(
                loopPolicy,
                new PolicyToolCallContext("t1", "s1", true, null, "perga", "perga_task_ls", ParseJson("{}"), ParseJson("{}")),
                ParseJson("{}")));
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
              guardToolCall: (ctx, call) => {
                if (call.toolName === "ask_no_opts") {
                  return AskHuman(`Confirm on ${ctx.inputs.envName}?`);
                }
                if (call.toolName === "deny_tool") {
                  return Deny(`Blocked on ${ctx.inputs.envName}`);
                }
                return Allow();
              },
              schedules: [
                { id: "known", cron: "0 * * * *", wakePrompt: "Ping" }
              ]
            });
            """);

        var emptyInputs = ParseJson("{}");
        var state = _vm.InitState(compiled, emptyInputs).NextState;

        var askNoOpts = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("t1", "s1", true, null, "perga", "ask_no_opts", emptyInputs, emptyInputs),
            state);
        var ask = Assert.IsType<GuardDecision.AskHuman>(askNoOpts.Output);
        Assert.Equal("Confirm on staging?", ask.Title);
        Assert.Empty(ask.Options);

        var denyRes = _vm.GuardToolCall(
            compiled,
            new PolicyToolCallContext("t1", "s1", true, null, "perga", "deny_tool", emptyInputs, emptyInputs),
            state);
        var deny = Assert.IsType<GuardDecision.Deny>(denyRes.Output);
        Assert.Equal("Blocked on staging", deny.Reason);

        var unknownSched = _vm.BuildScheduleWakePrompt(compiled, "missing-schedule", emptyInputs, state);
        Assert.Null(unknownSched.Output);
    }
}
