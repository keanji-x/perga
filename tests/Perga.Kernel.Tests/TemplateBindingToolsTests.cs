using System.Runtime.CompilerServices;
using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class TemplateBindingToolsTests
{
    private const string InlineSource = """
        export default defineTemplate({
          id: 'inline', title: 'Inline',
          inputs: { unit: { type: 'string', default: 'km' } },
          report: { initialMarkdown: '# Initial\n\nUnits: ${inputs.unit}' }
        });
        """;

    [Fact]
    public async Task TrackSetPreviewsAndSeedsBlankReportWithInterpolatedMarkdown()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync();

        var preview = await Call(ctx, track, KernelMcpToolNames.TrackSet, new
        {
            source = InlineSource, inputs = new { unit = "m" }, dryRun = true
        });
        Assert.False(preview.TryGetProperty("workspaceSpec", out _));
        Assert.Equal("seed", preview.GetProperty("plan").GetProperty("reportAction").GetString());
        Assert.Equal("init", preview.GetProperty("plan").GetProperty("dataAction").GetString());
        Assert.Empty((await ctx.Kernel.ReadReportAsync(track.Track.Id)).Blocks);

        var result = await Call(ctx, track, KernelMcpToolNames.TrackSet, new { source = InlineSource, seedReport = false });
        Assert.Equal("keep", result.GetProperty("plan").GetProperty("reportAction").GetString());
        Assert.Empty((await ctx.Kernel.ReadReportAsync(track.Track.Id)).Blocks);

        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { source = InlineSource, inputs = new { unit = "m" } });
        var report = await ctx.Kernel.ReadReportAsync(track.Track.Id);
        Assert.Equal(1, report.DocRev);
        Assert.Contains("Units: m", report.RenderedMarkdown);
        Assert.Single(ctx.Store.PersistedEvents, evt => evt.Kind == "report.updated");

        var conflict = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, track, KernelMcpToolNames.TrackSet, new { source = InlineSource, seedReport = true }));
        Assert.Equal(KernelErrorCode.Conflict, conflict.Code);
    }

    [Theory]
    [InlineData("template.perga.js")]
    [InlineData("template.perga.json")]
    public async Task TrackSetReadsWorkspacePackageAndProjectsFiles(string entry)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync(cwd: ctx.DataDir);
        var folder = Directory.CreateDirectory(Path.Combine(track.Track.Cwd, "authoring"));
        var source = entry.EndsWith(".json", StringComparison.Ordinal)
            ? """{"id":"local-json","title":"Local JSON","report":{"template":"./report.md"}}"""
            : "export default defineTemplate({ id: 'local-js', title: 'Local JS', report: { template: './report.md' } });";
        const string markdown = "# 本地模板\n\n报告正文";
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, entry), source);
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "report.md"), markdown);

        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { path = "authoring" });
        var summary = await Call(ctx, track, KernelMcpToolNames.TemplateRead, new { templateId = "__current__" });
        Assert.False(summary.TryGetProperty("snapshot", out _));
        Assert.Equal(new[] { "report.md", entry }, summary.GetProperty("files").EnumerateArray().Select(file => file.GetString()));
        var files = await Call(ctx, track, KernelMcpToolNames.TemplateRead, new { templateId = "__current__", format = "files" });
        Assert.Equal(source, files.GetProperty("files").GetProperty(entry).GetString());
        var single = await Call(ctx, track, KernelMcpToolNames.TemplateRead, new { templateId = "__current__", format = "files", file = "report.md" });
        Assert.False(single.TryGetProperty("data", out _));
        Assert.False(single.TryGetProperty("manifest", out _));
        Assert.Contains("本地模板", single.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(markdown, Assert.Single(single.GetProperty("files").EnumerateObject()).Value.GetString());
        var missing = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, track, KernelMcpToolNames.TemplateRead, new { templateId = "__current__", format = "files", file = "other.md" }));
        Assert.Equal(KernelErrorCode.NotFound, missing.Code);
    }

    [Fact]
    public async Task TrackSetAcceptsSingleFileAndRejectsPathEscapesAndMultipleSelectors()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var workspace = Directory.CreateDirectory(Path.Combine(ctx.DataDir, "workspace"));
        var (_, track) = await ctx.CreateDefaultTrackAsync(cwd: workspace.FullName);
        var file = Path.Combine(workspace.FullName, "inline.perga.js");
        await File.WriteAllTextAsync(file, InlineSource);
        var outside = Path.Combine(ctx.DataDir, "outside.perga.js");
        await File.WriteAllTextAsync(outside, InlineSource);
        // Windows development accounts may lack the OS privilege to create symbolic links.
        if (!OperatingSystem.IsWindows())
            File.CreateSymbolicLink(Path.Combine(workspace.FullName, "linked.perga.js"), outside);

        var prohibited = new List<string> { "../outside.perga.js", outside };
        if (!OperatingSystem.IsWindows()) prohibited.Add("linked.perga.js");
        foreach (var path in prohibited)
        {
            var denied = await Assert.ThrowsAsync<KernelException>(() =>
                Call(ctx, track, KernelMcpToolNames.TrackSet, new { path }));
            Assert.Equal(KernelErrorCode.InvalidArgument, denied.Code);
        }
        var conflict = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, track, KernelMcpToolNames.TrackSet, new { path = file, source = InlineSource }));
        Assert.Equal(KernelErrorCode.InvalidArgument, conflict.Code);

        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { path = file });
        var files = await Call(ctx, track, KernelMcpToolNames.TemplateRead, new { templateId = "__current__", format = "files" });
        Assert.Equal(InlineSource, files.GetProperty("files").GetProperty("template.perga.js").GetString());
    }

    [Fact]
    public async Task ReadOnlyAndLeadClearBypassBeforeWhileWritesReceiveSessionContext()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync();
        var lead = track.Track.LeadSessionId;
        await Call(ctx, track, KernelMcpToolNames.TrackSet, new
        {
            source = """
                export default defineTemplate({
                  id: 'guard', title: 'Guard',
                  before: (action, ctx) => {
                    if (!ctx.session || !ctx.session.isLead || !ctx.session.sessionId)
                      return Deny('session missing');
                    if (action.type === 'report.commit' || action.type === 'tool.report_commit')
                      return Allow();
                    return Deny('guarded write');
                  }
                });
                """
        });
        var report = await ctx.Kernel.ReadReportAsync(track.Track.Id);
        await ctx.Kernel.CommitReportAsync(track.Track.Id, lead!, new(report.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "# Overview", null)]));
        await Call(ctx, track, KernelMcpToolNames.TrackStatus, new { });
        await Call(ctx, track, KernelMcpToolNames.TemplateRead, new { templateId = "__current__" });
        await Call(ctx, track, KernelMcpToolNames.FlowRead, new { });

        var deniedRead = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, track, KernelMcpToolNames.TaskList, new { }));
        Assert.Equal(KernelErrorCode.PolicyDenied, deniedRead.Code);

        var denied = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, track, KernelMcpToolNames.TrackSet, new { source = InlineSource }));
        Assert.Equal(KernelErrorCode.PolicyDenied, denied.Code);
        Assert.Contains("guarded write", denied.Message);

        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { clear = true, dryRun = true });
        Assert.Equal("guard", (await ctx.Kernel.GetTrackAggregateAsync(track.Track.Id)).Track.TemplateId);
        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { clear = true });
        Assert.Null((await ctx.Kernel.GetTrackAggregateAsync(track.Track.Id)).Track.TemplateId);
    }

    [Fact]
    public async Task HeaderDiagnosticAndProviderHintSurfaceInMcpAndUnknownNullInputIsRejected()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, []), new("claude", true, "1", null, ["other-model"])];
        var (_, track) = await ctx.CreateDefaultTrackAsync();
        const string source = """
            export default defineTemplate({
              id: 'diagnostics', title: 'Diagnostics',
              inputs: { model: { type: 'string', optional: true } },
              headerView: () => { throw new Error('cannot render header'); },
              setup: inputs => ({ tasks: {
                routing: inputs.model ? [{ when: {}, model: inputs.model }] : []
              }})
            });
            """;
        var preview = await Call(ctx, track, KernelMcpToolNames.TrackSet, new { source, dryRun = true });
        Assert.Contains(preview.GetProperty("warnings").EnumerateArray(), warning => warning.GetString()!.Contains("cannot render header"));
        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { source });

        var invalid = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, track,
            KernelMcpToolNames.TemplateUpdateInputs, new { inputs = new { typo = (string?)null } }));
        Assert.Equal(KernelErrorCode.InvalidArgument, invalid.Code);
        Assert.Contains("typo", invalid.Message);
        var result = await Call(ctx, track, KernelMcpToolNames.TemplateUpdateInputs, new { inputs = new { model = "other-model" } });
        var warnings = result.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()).ToArray();
        Assert.Contains(warnings, warning => warning!.Contains("Specify provider 'claude'"));
        Assert.Contains(warnings, warning => warning!.Contains("cannot render header"));
        var current = await Call(ctx, track, KernelMcpToolNames.TemplateRead, new { templateId = "__current__" });
        Assert.Contains("cannot render header", current.GetProperty("headerViewError").GetString());
    }

    [Fact]
    public async Task FlowCommitPersistsRoutedAccessAndAdmitsReviewWithoutGate()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync();
        await Call(ctx, track, KernelMcpToolNames.TrackSet, new
        {
            source = """
                export default defineTemplate({
                  id: 'review-policy', title: 'Review',
                  tasks: { requireGateForReadWrite: true, routing: [
                    { when: { role: 'review' }, access: 'read_only', enforce: true }
                  ] },
                  before: (action) => action.type === 'task.dispatch' && action.access !== 'read_only'
                    ? Deny('expected a read-only reviewer') : Allow()
                });
                """
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var releaseLead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, input, token) => RunTurn(input, releaseLead.Task, token);
        var human = new InteropPrincipal.Human();
        var leadId = track.Track.LeadSessionId!;
        await ctx.Kernel.SendSessionMessageAsync(leadId, new("Hold for Flow commit", "hold-flow-commit"), human, ct);
        var lead = await ctx.Kernel.GetSessionItemsAsync(leadId, new(), human, ct);
        while (lead.ActiveTurn is null)
        {
            await Task.Delay(5, ct);
            lead = await ctx.Kernel.GetSessionItemsAsync(leadId, new(), human, ct);
        }
        var caller = ctx.Kernel.McpSessionBridge.RegisterSession(track.Track.Id, leadId, true, null, null, []).Principal
            with { ExecutionOrigin = new(lead.ActiveTurn.Id) };
        try
        {
            var commit = await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.FlowCommit,
                TestJson.FromObject(new
                {
                    content = """
                        { "rev": 0, "nodes": [{ "id": "review", "do": {
                          "kind": "run", "role": "review", "prompt": "Review this change"
                        }}] }
                        """
                }), ct);
            Assert.True(commit.GetProperty("ok").GetBoolean(), commit.GetRawText());
        }
        finally
        {
            releaseLead.TrySetResult();
        }
        await ctx.Kernel.WaitForTrackIdleAsync(track.Track.Id, ct);
        var saved = (await ctx.Store.LoadTrackAggregateAsync(track.Track.Id, default))!;
        var task = Assert.Single(saved.Tasks, task => task.TaskKey == "review");
        Assert.Equal(AccessMode.ReadOnly, task.Access);
        Assert.NotNull(task.SessionId);
        Assert.NotEqual(TaskRunStatus.Failed, task.Status);

        var flowRead = await Call(ctx, track, KernelMcpToolNames.FlowRead, new { });
        var projectedNode = Assert.Single(flowRead.GetProperty("projection").GetProperty("nodes").EnumerateArray());
        Assert.Equal("read_only", projectedNode.GetProperty("access").GetString());
    }

    [Fact]
    public async Task HostCommandNodeCannotAcquireRoutedReadOnlyLease()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync();
        await Call(ctx, track, KernelMcpToolNames.TrackSet, new
        {
            source = """
                export default defineTemplate({
                  id: 'cmd-fence', title: 'Cmd Fence',
                  tasks: {
                    requireGateForReadWrite: true,
                    routing: [{ when: {}, access: 'read_only', enforce: true }]
                  }
                });
                """
        });

        var denied = await ctx.Kernel.CommitTrackFlowAsync(track.Track.Id, dryRun: true, expectedFlowRev: 0,
            caller: new InteropPrincipal.Human(),
            content: """
                { "rev": 0, "nodes": [{ "id": "shell-cmd", "do": {
                  "kind": "run", "cmd": "echo hi"
                }}] }
                """);
        Assert.False(denied.Ok);
        Assert.Contains(denied.Diagnostics, d => d.Message.Contains("denied", StringComparison.OrdinalIgnoreCase));

        // Even with a non-enforced wildcard read_only rule, omitted-access host commands stay read_write and require a gate.
        await Call(ctx, track, KernelMcpToolNames.TrackSet, new
        {
            source = """
                export default defineTemplate({
                  id: 'cmd-fence-soft', title: 'Cmd Fence Soft',
                  tasks: {
                    requireGateForReadWrite: true,
                    routing: [{ when: {}, access: 'read_only', enforce: false }]
                  }
                });
                """
        });
        var needsGate = await ctx.Kernel.CommitTrackFlowAsync(track.Track.Id, dryRun: true, expectedFlowRev: 0,
            caller: new InteropPrincipal.Human(),
            content: """
                { "rev": 0, "nodes": [{ "id": "shell-cmd", "do": {
                  "kind": "run", "cmd": "echo hi"
                }}] }
                """);
        Assert.False(needsGate.Ok);
        Assert.Contains(needsGate.Diagnostics, d => d.Message.Contains("gate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NonRegularFifoTemplateFileIsRejectedWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var workspace = Directory.CreateDirectory(Path.Combine(ctx.DataDir, "fifo-ws"));
        var (_, track) = await ctx.CreateDefaultTrackAsync(cwd: workspace.FullName);
        var fifoPath = Path.Combine(workspace.FullName, "pipe.perga.js");
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mkfifo", fifoPath)
        {
            UseShellExecute = false
        })!;
        await proc.WaitForExitAsync();
        Assert.Equal(0, proc.ExitCode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel,
                new McpSessionPrincipal("token", track.Track.Id, track.Track.LeadSessionId!, true, null, null, []),
                KernelMcpToolNames.TrackSet,
                TestJson.FromObject(new { path = "pipe.perga.js", dryRun = true }),
                cts.Token));
        Assert.Equal(KernelErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("readable file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InheritedSnapshotInspectFilesPreservesParentAndChildWithoutCollision()
    {
        var parentSource = "export default defineTemplate({ id: 'parent', title: 'Parent' });";
        var childSource = "export default defineTemplate({ id: 'child', title: 'Child' });";
        var snapshot = JsonSerializer.Serialize(new { format = "perga.snapshot.v1", sources = new[] { parentSource, childSource } });
        var files = global::Perga.PolicyVm.TemplateBundle.Parse(snapshot).InspectFiles();
        Assert.Equal(childSource, files["template.perga.js"]);
        Assert.Equal(parentSource, files["base/template.perga.js"]);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> RunTurn(TurnInput input, Task releaseLead,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (input.Prompt.Contains("Hold for Flow commit", StringComparison.Ordinal))
            await releaseLead.WaitAsync(ct);
        yield return new UnifiedAgentEvent.ThreadBound("thread-flow-routing");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(10, 5, 0, 100_000));
    }

    [Fact]
    public async Task TaskAndReportReadsInterpolateUpdatedInputsWithoutRewritingTasks()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync();
        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { source = InlineSource });
        await FlowFixture.AddAsync(ctx.Kernel, track.Track.Id, new FlowNodeSpecDto("scheduled",
            new("run", Prompt: "Check ${inputs.unit}", Acceptance: "Verify ${inputs.unit}"),
            When: new(At: "2099-01-01T00:00:00Z")));
        await Call(ctx, track, KernelMcpToolNames.TemplateUpdateInputs, new { inputs = new { unit = "m" } });

        var tasks = await Call(ctx, track, KernelMcpToolNames.TaskList, new { });
        var report = await Call(ctx, track, KernelMcpToolNames.ReportRead, new { });
        foreach (var task in new[] { tasks[0], report.GetProperty("tasks")[0] })
        {
            Assert.Equal("Check m", task.GetProperty("goalOrCmd").GetString());
            Assert.Equal("Verify m", task.GetProperty("acceptance").GetString());
        }
        var saved = (await ctx.Store.LoadTrackAggregateAsync(track.Track.Id, default))!;
        Assert.Equal("Check ${inputs.unit}", Assert.Single(saved.Tasks).GoalOrCmd);
    }

    [Fact]
    public async Task ReportValidatesNewWidgetsAndTemplateClearWarnsAboutInvalidatedViews()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync();
        await Call(ctx, track, KernelMcpToolNames.TrackSet, new
        {
            source = """
                export default defineTemplate({
                  id: 'widget-test', title: 'Widgets',
                  report: { views: { rows: () => ({ items: [] }) } }
                });
                """
        });
        var read = await ctx.Kernel.ReadReportAsync(track.Track.Id);
        var malformed = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(track.Track.Id, track.Track.LeadSessionId!, new(read.Anchor,
                [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Widget, null,
                    TestJson.Parse("""{"use":"builtin:list","view":"rows","height":2401}"""))])));
        Assert.Equal(KernelErrorCode.InvalidArgument, malformed.Code);
        Assert.Equal(read.DocRev, (await ctx.Kernel.ReadReportAsync(track.Track.Id)).DocRev);

        var missingAsset = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(track.Track.Id, track.Track.LeadSessionId!, new(read.Anchor,
                [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Widget, null,
                    TestJson.Parse("""{"use":"./widgets/missing.html","view":"rows"}"""))])));
        Assert.Equal(KernelErrorCode.InvalidArgument, missingAsset.Code);
        Assert.Contains("missing", missingAsset.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(read.DocRev, (await ctx.Kernel.ReadReportAsync(track.Track.Id)).DocRev);

        var unknownView = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(track.Track.Id, track.Track.LeadSessionId!, new(read.Anchor,
                [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Widget, null,
                    TestJson.Parse("""{"use":"builtin:list","view":"nonexistent"}"""))])));
        Assert.Equal(KernelErrorCode.InvalidArgument, unknownView.Code);
        Assert.Equal(read.DocRev, (await ctx.Kernel.ReadReportAsync(track.Track.Id)).DocRev);

        var result = await ctx.Kernel.CommitReportAsync(track.Track.Id, track.Track.LeadSessionId!, new(read.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Widget, null, TestJson.Parse("""{"use":"builtin:list","view":"rows"}"""))]));
        Assert.Empty(result.Diagnostics);
        var widgetBlockId = Assert.Single(result.Blocks).Id;

        var clearPreview = await Call(ctx, track, KernelMcpToolNames.TrackSet, new { clear = true, dryRun = true });
        var warnings = clearPreview.GetProperty("warnings").EnumerateArray().ToArray();
        Assert.Single(warnings);
        Assert.Contains("View 'rows' is not declared", warnings[0].GetString());
        Assert.Equal("widget-test", (await ctx.Kernel.GetTrackAggregateAsync(track.Track.Id)).Track.TemplateId);

        // Detaching leaves a historical broken widget: report_read reports diagnostics, unrelated prose edits succeed,
        // and deleting the broken widget clears the diagnostic.
        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { clear = true });
        var afterClear = await ctx.Kernel.ReadReportAsync(track.Track.Id);
        Assert.Equal("unknown_widget_view", Assert.Single(afterClear.Diagnostics).Code);
        var proseEdit = await ctx.Kernel.CommitReportAsync(track.Track.Id, track.Track.LeadSessionId!, new(afterClear.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "# Notes\n\nStill editable", null)]));
        Assert.Equal("unknown_widget_view", Assert.Single(proseEdit.Diagnostics).Code);
        var cleaned = await ctx.Kernel.CommitReportAsync(track.Track.Id, track.Track.LeadSessionId!, new(proseEdit.Anchor,
            [new BlockOp.DeleteBlockOp(widgetBlockId)]));
        Assert.Empty(cleaned.Diagnostics);
    }

    [Fact]
    public async Task ReportWidgetValidationUsesInputDependentSetupViews()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync();
        const string source = """
            export default defineTemplate({
              id: 'dynamic-views', title: 'Dynamic Views',
              inputs: { enabled: { type: 'boolean', default: false } },
              setup: inputs => inputs.enabled
                ? { report: { views: { rows: () => ({ items: [] }) } } }
                : {}
            });
            """;

        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { source, inputs = new { enabled = true } });
        var read = await ctx.Kernel.ReadReportAsync(track.Track.Id);
        var committed = await ctx.Kernel.CommitReportAsync(track.Track.Id, track.Track.LeadSessionId!, new(read.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Widget, null,
                TestJson.Parse("""{"use":"builtin:list","view":"rows"}"""))]));
        Assert.Empty(committed.Diagnostics);
        Assert.Empty((await ctx.Kernel.ReadReportAsync(track.Track.Id)).Diagnostics);

        await Call(ctx, track, KernelMcpToolNames.TemplateUpdateInputs, new { inputs = new { enabled = false } });
        var afterDisable = await ctx.Kernel.ReadReportAsync(track.Track.Id);
        Assert.Equal("unknown_widget_view", Assert.Single(afterDisable.Diagnostics).Code);
    }

    private static Task<JsonElement> Call(KernelTestContext ctx, TrackAggregateSnapshot track, string tool, object arguments) =>
        KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel,
            new McpSessionPrincipal("token", track.Track.Id, track.Track.LeadSessionId!, true, null, null, []),
            tool, TestJson.FromObject(arguments));
}
