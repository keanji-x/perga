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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TrackSetPreservesSummaryWhenSeedingBlankContent(bool summaryOnly, bool explicitSeed)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync();
        var initial = await Call(ctx, track, KernelMcpToolNames.ReportRead, new { });
        var originalBlocks = new[] { new { op = "upsert", blockKind = "prose", markdown = "Old content" } };
        await Call(ctx, track, KernelMcpToolNames.ReportCommit, new
        {
            anchor = initial.GetProperty("anchor"),
            ops = summaryOnly ? [] : originalBlocks,
            summary = "Previous summary"
        });
        var populated = await Call(ctx, track, KernelMcpToolNames.ReportRead, new { });
        Assert.Equal(1, populated.GetProperty("docRev").GetInt32());
        if (!summaryOnly)
        {
            var block = Assert.Single(populated.GetProperty("blocks").EnumerateArray());
            await Call(ctx, track, KernelMcpToolNames.ReportCommit, new
            {
                anchor = populated.GetProperty("anchor"),
                ops = new[] { new { op = "delete", id = block.GetProperty("id").GetString() } }
            });
        }
        var blankRev = summaryOnly ? 1 : 2;
        var emptied = await ctx.Kernel.ReadReportAsync(track.Track.Id);
        Assert.Equal(blankRev, emptied.DocRev);
        Assert.Empty(emptied.Blocks);
        Assert.Empty(emptied.RenderedMarkdown);
        Assert.Equal("Previous summary", emptied.Summary);

        var preview = await Call(ctx, track, KernelMcpToolNames.TrackSet, new { source = InlineSource, dryRun = true });
        Assert.Equal("seed", preview.GetProperty("plan").GetProperty("reportAction").GetString());
        Assert.Equal(blankRev, (await ctx.Kernel.ReadReportAsync(track.Track.Id)).DocRev);

        object args = explicitSeed ? new { source = InlineSource, seedReport = true } : new { source = InlineSource };
        var bound = await Call(ctx, track, KernelMcpToolNames.TrackSet, args);
        Assert.Equal("seed", bound.GetProperty("plan").GetProperty("reportAction").GetString());
        var seeded = await ctx.Kernel.ReadReportAsync(track.Track.Id);
        Assert.Equal(blankRev + 1, seeded.DocRev);
        Assert.Equal("Previous summary", seeded.Summary);
        Assert.Contains("Units: km", seeded.RenderedMarkdown, StringComparison.Ordinal);
        Assert.NotEmpty(seeded.Blocks);
    }

    [Fact]
    public async Task TrackSetRequiresExactPackageEntryCaseOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync(cwd: ctx.DataDir);
        var folder = Directory.CreateDirectory(Path.Combine(track.Track.Cwd, "package"));
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "Template.Perga.Js"), InlineSource);

        var error = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, track, KernelMcpToolNames.TrackSet, new { path = "package/template.perga.js" }));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
        Assert.Contains("template.perga.js", error.Message, StringComparison.Ordinal);
        Assert.Null((await ctx.Kernel.GetTrackAggregateAsync(track.Track.Id)).Track.TemplateId);

        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { path = "package/Template.Perga.Js" });
        Assert.Equal("inline", (await ctx.Kernel.GetTrackAggregateAsync(track.Track.Id)).Track.TemplateId);
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

    [Theory]
    [InlineData("template.perga.js", "local-js")]
    [InlineData("Template.Perga.Js", "local-js")]
    [InlineData("template.perga.json", "local-json")]
    public async Task TrackSetPacksSiblingsAndUsesRequestedPackageEntry(string entry, string templateId)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, track) = await ctx.CreateDefaultTrackAsync(cwd: ctx.DataDir);
        var folder = Directory.CreateDirectory(Path.Combine(track.Track.Cwd, "tmp_local_pkg"));
        const string markdown = "Report from sibling Markdown";
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "report.md"), markdown);
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "views.js"), "export const rows = () => ({ count: 3 });");
        var jsEntry = templateId == "local-js" ? entry : "template.perga.js";
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, jsEntry), """
            import { rows } from './views.js';
            export default defineTemplate({
              id: 'local-js', title: 'Local JS',
              report: { template: './report.md', views: { rows } }
            });
            """);
        await File.WriteAllTextAsync(Path.Combine(folder.FullName, "template.perga.json"),
            """{"id":"local-json","title":"Local JSON","report":{"template":"./report.md"}}""");

        await Call(ctx, track, KernelMcpToolNames.TrackSet, new { path = $"tmp_local_pkg/{entry}" });
        Assert.Equal(templateId, (await ctx.Kernel.GetTrackAggregateAsync(track.Track.Id)).Track.TemplateId);
        Assert.Equal(markdown, Assert.Single((await ctx.Kernel.ReadReportAsync(track.Track.Id)).Blocks).Markdown);
        if (templateId == "local-js")
        {
            var views = await ctx.Kernel.GetTrackViewsAsync(track.Track.Id);
            Assert.Equal(3, views.Views["rows"].GetProperty("count").GetInt32());
        }
    }

    [Theory]
    [InlineData("template.perga.js")]
    [InlineData("pkg/custom.perga.js")]
    public async Task TrackSetAcceptsSingleFileAndRejectsPathEscapesAndMultipleSelectors(string standalonePath)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var workspace = Directory.CreateDirectory(Path.Combine(ctx.DataDir, "workspace"));
        var (_, track) = await ctx.CreateDefaultTrackAsync(cwd: workspace.FullName);
        var file = Path.Combine(workspace.FullName, standalonePath);
        var fileFolder = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, InlineSource);
        await File.WriteAllTextAsync(Path.Combine(fileFolder.FullName, "sibling.md"), "Standalone files must not pack siblings");
        var outside = Path.Combine(ctx.DataDir, "template.perga.js");
        await File.WriteAllTextAsync(outside, InlineSource);
        var package = Directory.CreateDirectory(Path.Combine(workspace.FullName, "linked-pkg"));
        var prohibited = new List<string> { "linked-pkg/../../template.perga.js", outside };
        // Windows development accounts may lack the OS privilege to create symbolic links.
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(package.FullName, "template.perga.js"), outside);
            Directory.CreateSymbolicLink(Path.Combine(workspace.FullName, "linked-dir"), ctx.DataDir);
            prohibited.Add("linked-pkg/template.perga.js");
            prohibited.Add("linked-dir/template.perga.js");
        }
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
        var source = Assert.Single(files.GetProperty("files").EnumerateObject());
        Assert.Equal("template.perga.js", source.Name);
        Assert.Equal(InlineSource, source.Value.GetString());
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

        await Call(ctx, track, KernelMcpToolNames.TaskList, new { });

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
        var caller = ctx.RuntimeCaller(leadId);
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
            var node = Assert.Single(commit.GetProperty("flow").GetProperty("nodes").EnumerateArray());
            Assert.False(node.TryGetProperty("prompt", out _));
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
        var caller = await ctx.OpenMcpCallerAsync(track.Track.LeadSessionId!, cts.Token);
        var ex = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller,
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
            Title: "Scheduled check",
            When: new(At: "2099-01-01T00:00:00Z")));
        await Call(ctx, track, KernelMcpToolNames.TemplateUpdateInputs, new { inputs = new { unit = "m" } });

        var tasks = await Call(ctx, track, KernelMcpToolNames.TaskList, new { });
        Assert.Equal("Check m", tasks[0].GetProperty("goalOrCmd").GetString());
        Assert.Equal("Verify m", tasks[0].GetProperty("acceptance").GetString());

        var kernelTask = Assert.Single((await ctx.Kernel.ReadReportAsync(track.Track.Id)).Tasks);
        Assert.Equal("Check m", kernelTask.GoalOrCmd);
        Assert.Equal("Verify m", kernelTask.Acceptance);

        var report = await Call(ctx, track, KernelMcpToolNames.ReportRead, new { });
        var blocks = await Call(ctx, track, KernelMcpToolNames.ReportRead, new { format = "blocks" });
        var receipt = await Call(ctx, track, KernelMcpToolNames.ReportCommit, new
        {
            anchor = report.GetProperty("anchor"),
            ops = new[] { new { op = "upsert", blockKind = "prose", markdown = "# Notes\n\nVerified." } }
        });
        foreach (var response in new[] { report, blocks, receipt })
        {
            var task = Assert.Single(response.GetProperty("tasks").EnumerateArray());
            Assert.Equal(kernelTask.Id, task.GetProperty("id").GetString());
            Assert.Equal("scheduled", task.GetProperty("taskKey").GetString());
            Assert.Equal("Scheduled check", task.GetProperty("title").GetString());
            Assert.Equal("pending", task.GetProperty("status").GetString());
            Assert.False(task.TryGetProperty("dependsOn", out _));
            Assert.False(task.TryGetProperty("goalOrCmd", out _));
            Assert.False(task.TryGetProperty("acceptance", out _));
            Assert.False(task.TryGetProperty("context", out _));
            Assert.False(task.TryGetProperty("workerResult", out _));
            Assert.False(task.TryGetProperty("gateVerdict", out _));
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

        // Widgets authored with JSON in block.Markdown (payload: null) must validate identically.
        await Call(ctx, track, KernelMcpToolNames.TemplateUpdateInputs, new { inputs = new { enabled = true } });
        var reenabled = await ctx.Kernel.ReadReportAsync(track.Track.Id);
        var fromMarkdown = await ctx.Kernel.CommitReportAsync(track.Track.Id, track.Track.LeadSessionId!, new(reenabled.Anchor,
            [new BlockOp.UpsertBlockOp("b_md_widget", ReportBlockKind.Widget, """{"use":"builtin:list","view":"rows"}""", null)]));
        Assert.Empty(fromMarkdown.Diagnostics);
        await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(track.Track.Id, track.Track.LeadSessionId!, new(fromMarkdown.Anchor,
                [new BlockOp.UpsertBlockOp("b_md_bad", ReportBlockKind.Widget, """{"use":"builtin:list","view":"missing"}""", null)])));
    }

    private static async Task<JsonElement> Call(KernelTestContext ctx, TrackAggregateSnapshot track, string tool, object arguments)
    {
        var caller = await ctx.OpenMcpCallerAsync(track.Track.LeadSessionId!);
        return await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, tool, TestJson.FromObject(arguments));
    }
}
