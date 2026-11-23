using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class PolicyAdmissionRegressionTests
{
    private static Task<System.Text.Json.JsonElement> SetPolicy(KernelTestContext ctx, McpSessionPrincipal caller, string source) =>
        KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source }));

    [Fact]
    public async Task ProviderDiscoveryBypassesTemplatePolicyWithoutUpdatingDataOrRequestingApproval()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, """
            export default {
              id: 'discovery', title: 'Discovery',
              before(call, ctx) {
                if (call.type !== 'tool.provider_list') return Allow();
                ctx.data.before = true;
                if (call.arguments.refresh) return AskHuman('Refresh?', {approve: 'Yes', deny: 'No'});
                return Allow();
              }
            };
            """);
        var before = (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.Data.GetRawText();
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.ProviderList, TestJson.FromObject(new { }));
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller,
            KernelMcpToolNames.ProviderList, TestJson.FromObject(new { refresh = true }));
        var after = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId);
        Assert.Equal(before, after.Track.Data.GetRawText());
        Assert.Empty(after.PendingApprovals);
        Assert.Equal(2, ctx.AgentRuntime.ProviderProbeCalls);
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("update")]
    public async Task PolicyChangeDuringProbeFencesMutation(string operation)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var track = snapshot.Track;
        var leadId = track.LeadSessionId!;
        var human = new InteropPrincipal.Human();
        var child = (await ctx.Kernel.CreateSessionAsync(track.Id, new(), "existing-child", human, ct)).Receipt.Session;
        ctx.AgentRuntime.TurnScript = (_, _, token) => BlockUntilStopped(token);
        await ctx.Kernel.SendSessionMessageAsync(leadId, new("hold", "hold"), human, ct);
        var lead = await ctx.Kernel.GetSessionItemsAsync(leadId, new(), human, ct);
        while (lead.ActiveTurn is null)
        {
            await Task.Delay(5, ct);
            lead = await ctx.Kernel.GetSessionItemsAsync(leadId, new(), human, ct);
        }
        var caller = ctx.RuntimeCaller(leadId);
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.ProbeScript = async token =>
        {
            probing.TrySetResult();
            await release.Task.WaitAsync(token);
            return ctx.AgentRuntime.Providers;
        };
        var tool = operation switch
        {
            "update" => KernelMcpToolNames.SessionUpdate,
            _ => KernelMcpToolNames.SessionCreate
        };
        var args = operation switch
        {
            "update" => TestJson.FromObject(new { sessionId = child.Id, expectedConfigVersion = 0, model = "chosen" }),
            _ => TestJson.FromObject(new { title = "conversation" })
        };
        var pending = KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, tool, args, ct);
        try
        {
            await probing.Task.WaitAsync(ct);
            await SetPolicy(ctx, caller, "export default {id:'new-policy',title:'New policy'};").WaitAsync(ct);
            release.TrySetResult();
            var error = await Assert.ThrowsAsync<KernelException>(() => pending.WaitAsync(ct));
            Assert.Equal(KernelErrorCode.PolicyDenied, error.Code);
            var persisted = (await ctx.Store.LoadTrackAggregateAsync(track.Id, ct))!;
            Assert.Equal(2, persisted.Sessions.Count);
            Assert.Empty(persisted.Tasks);
            Assert.Equal(child.ConfigVersion, Assert.Single(persisted.Sessions, session => session.Id == child.Id).ConfigVersion);
        }
        finally
        {
            release.TrySetResult();
            await ctx.Kernel.ControlSessionAsync(leadId, new("stop", TurnId: lead.ActiveTurn.Id), human, ct);
        }
    }

    [Fact]
    public async Task CancellationWhileAwaitingApproval_ExpiresReportApprovals()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, "export default {id:'ask',title:'Ask',before:a=>a.type==='report.commit'?AskHuman('Approve?',{approve:'Approve',deny:'Reject'}):Allow()};");
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        using var cancellation = new CancellationTokenSource();
        var commit = ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("note", ReportBlockKind.Prose, "Note", null)]), cancellation.Token, await ctx.AuthorizeAsync(caller));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        TrackAggregateSnapshot pending;
        do
        {
            await Task.Delay(10, timeout.Token);
            pending = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token);
        } while (pending.PendingApprovals.Count == 0);
        var approval = Assert.Single(pending.PendingApprovals);
        var overlay = Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks);
        Assert.Equal(1, overlay.PendingApprovalCount);
        Assert.Equal(0, overlay.OpenAskCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        Assert.Equal(0, Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks).PendingApprovalCount);
        Assert.Empty((await ctx.Store.LoadTrackAggregateAsync(caller.TrackId, default))!.OpenAsks);
        var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.RespondInteractionAsync(approval.SessionId, approval.Id, new(ExecutionId: approval.ExecutionId, OptionId: approval.Options.Single(o => o.IsApproval).Id), new InteropPrincipal.Human(), default));
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
    }

    [Fact]
    public async Task ChangedPolicyDataInvalidatesAnOpenReportApproval()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, """
            export default { id: 'ask', title: 'Ask', data: { initial: { locked: false } },
              before(action, ctx) {
                if (action.type !== 'report.commit') return Allow();
                return ctx.data.locked ? Deny('Report locked') : AskHuman('Approve report?');
              } };
            """);
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commit = ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("note", ReportBlockKind.Prose, "Note", null)]), timeout.Token, await ctx.AuthorizeAsync(caller));
        ApprovalDto? approval = null;
        while (approval is null)
        {
            approval = (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token)).PendingApprovals.SingleOrDefault();
            if (approval is null) await Task.Delay(10, timeout.Token);
        }

        await ctx.Kernel.PatchTrackDataAsync(caller.TrackId, TestJson.Parse("""{"locked":true}"""), "test", ct: timeout.Token);
        await ctx.Kernel.RespondInteractionAsync(approval.SessionId, approval.Id, new(ExecutionId: approval.ExecutionId,
            OptionId: approval.Options.Single(o => o.IsApproval).Id), new InteropPrincipal.Human(), timeout.Token);
        var error = await Assert.ThrowsAsync<KernelException>(() => commit);
        Assert.Equal(KernelErrorCode.PolicyDenied, error.Code);
        Assert.Equal(report.DocRev, (await ctx.Kernel.ReadReportAsync(caller.TrackId, ct: timeout.Token)).DocRev);
    }

    [Theory]
    [InlineData("usage")]
    [InlineData("report")]
    [InlineData("none")]
    public async Task ReportApprovalAppliesOnlyToTheEvaluatedSessionContextAndPreview(string change)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!, ct);
        await SetPolicy(ctx, caller, """
            export default { id: 'report-approval', title: 'Report approval',
              before(action, ctx) {
                if (action.type !== 'report.commit' || !action.nextMarkdown.includes('Approval candidate')) return Allow();
                if (ctx.session.contextUsageRatio >= 0.8) return Deny('Session context is too full');
                return AskHuman('Approve report?');
              } };
            """);
        var human = new InteropPrincipal.Human();
        var before = await ctx.Kernel.ReadReportAsync(caller.TrackId, ct: ct);
        var authorization = await ctx.AuthorizeAsync(caller, ct);
        // A block-scoped proposal can still be applied after an independent report edit.
        var commit = ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(before.Anchor with { WholeDocumentRead = false },
                [new BlockOp.UpsertBlockOp("candidate", ReportBlockKind.Prose, "Approval candidate", null)]), ct, authorization);
        ApprovalDto? approval = null;
        while (approval is null)
        {
            if (commit.IsCompleted) await commit;
            approval = (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, ct)).PendingApprovals.SingleOrDefault();
            if (approval is null) await Task.Delay(10, ct);
        }

        if (change == "usage")
        {
            var runtime = Assert.Single(ctx.AgentRuntime.OpenedSessions);
            runtime.EmitIdleContextUsage(new IdleContextUsage(
                new TokenUsage(90_000, 0, 0, 100_000), ContextUsageRevision: 0, runtime.NativeThreadId));
            var current = await ctx.Kernel.GetSessionItemsAsync(caller.SessionId, new(), human, ct);
            Assert.Equal(0.9, current.Session.ContextUsage?.Ratio);
        }
        else if (change == "report")
        {
            await ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
                new(before.Anchor, [new BlockOp.UpsertBlockOp("other", ReportBlockKind.Prose, "Independent edit", null)]), ct);
        }

        Assert.Equal(caller.RuntimeBindingId, ctx.RuntimeCaller(caller.SessionId).RuntimeBindingId);
        Assert.Equal(authorization.AuthorizedPolicyGeneration, (await ctx.AuthorizeAsync(caller, ct)).AuthorizedPolicyGeneration);
        var beforeApproval = await ctx.Kernel.ReadReportAsync(caller.TrackId, ct: ct);
        await ctx.Kernel.RespondInteractionAsync(approval.SessionId, approval.Id, new(ExecutionId: approval.ExecutionId,
            OptionId: approval.Options.Single(o => o.IsApproval).Id), human, ct);

        if (change == "none")
        {
            var saved = await commit;
            Assert.Equal(beforeApproval.DocRev + 1, saved.DocRev);
            Assert.Contains(saved.Blocks, block => block.Markdown == "Approval candidate");
        }
        else
        {
            var error = await Assert.ThrowsAsync<KernelException>(() => commit);
            Assert.Equal(KernelErrorCode.PolicyDenied, error.Code);
            Assert.Equal(beforeApproval.DocRev, (await ctx.Kernel.ReadReportAsync(caller.TrackId, ct: ct)).DocRev);
            Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, ct)).PendingApprovals);
        }
    }

    [Fact]
    public async Task ChangedPolicyDataInvalidatesAnOpenVcsToolApproval()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, """
            export default { id: 'vcs-approval', title: 'VCS approval', data: { initial: { locked: false } },
              before(action, ctx) {
                if (action.type !== 'tool.restore_op') return Allow();
                return ctx.data.locked ? Deny('Workspace locked') : AskHuman('Restore workspace?');
              } };
            """);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var args = TestJson.Parse("""{"opId":"test-operation"}""");
        var guard = ctx.Kernel.GuardMcpToolCallAsync(caller, "vcs", "restore_op", args, ct);
        ApprovalDto? approval = null;
        while (approval is null)
        {
            approval = (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, ct)).PendingApprovals.SingleOrDefault();
            if (approval is null) await Task.Delay(10, ct);
        }

        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.DataPatch,
            TestJson.Parse("""{"patch":{"locked":true}}"""), ct);
        await ctx.Kernel.RespondInteractionAsync(approval.SessionId, approval.Id, new(ExecutionId: approval.ExecutionId,
            OptionId: approval.Options.Single(o => o.IsApproval).Id), new InteropPrincipal.Human(), ct);

        var error = await Assert.ThrowsAsync<KernelException>(() => guard.WaitAsync(ct));
        Assert.Equal(KernelErrorCode.PolicyDenied, error.Code);
        Assert.Equal("Active policy changed after authorization.", error.Message);
        var retry = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.GuardMcpToolCallAsync(caller, "vcs", "restore_op", args, ct));
        Assert.Equal(KernelErrorCode.PolicyDenied, retry.Code);
        Assert.Equal("Workspace locked", retry.Message);
    }

    [Fact]
    public async Task StopExpiresTemplateReportApprovalAndRejectsLateDecisions()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, "export default {id:'asks',title:'Asks',before:a=>a.type==='task.dispatch'||a.type==='report.commit'?AskHuman(a.type,{approve:'Approve',deny:'Reject'}):Allow()};");
        ctx.AgentRuntime.TurnScript = (_, _, ct) => BlockUntilStopped(ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = ctx.SendAndWaitForTurnAsync(caller.SessionId, new SendSessionMessageRequest("Wait", "template-stop"), timeout.Token);
        while ((await ctx.Kernel.GetSessionItemsAsync(caller.SessionId, new(), new InteropPrincipal.Human(), timeout.Token)).ActiveTurn is null)
            await Task.Delay(10, timeout.Token);
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        var commit = ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("note", ReportBlockKind.Prose, "Review.", null)]), timeout.Token, await ctx.AuthorizeAsync(caller));
        TrackAggregateSnapshot pending;
        do
        {
            await Task.Delay(10, timeout.Token);
            pending = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token);
        } while (pending.PendingApprovals.Count == 0);
        Assert.All(pending.PendingApprovals, approval => Assert.NotNull(approval.TurnId));
        await ctx.Kernel.ControlSessionAsync(caller.SessionId, new("stop", TurnId: (await ctx.Kernel.GetSessionItemsAsync(caller.SessionId, new(), new InteropPrincipal.Human(), default)).ActiveTurn!.Id), new InteropPrincipal.Human(), timeout.Token);
        var error = await Assert.ThrowsAsync<KernelException>(() => commit);
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
        foreach (var approval in pending.PendingApprovals)
        {
            var late = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.RespondInteractionAsync(approval.SessionId, approval.Id, new(ExecutionId: approval.ExecutionId, OptionId: approval.Options.Single(o => o.IsApproval).Id), new InteropPrincipal.Human(), timeout.Token));
            Assert.Equal(KernelErrorCode.Conflict, late.Code);
        }
        await running;
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        Assert.Empty((await ctx.Store.LoadTrackAggregateAsync(caller.TrackId, default))!.OpenAsks);
        Assert.Empty(await ctx.Kernel.ListTasksAsync(caller.TrackId));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> BlockUntilStopped(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    [Theory]
    [InlineData("report-deny")]
    [InlineData("report-throw")]
    [InlineData("checkpoints")]
    public async Task RejectedCommit_DoesNotLeaveApprovalAsks(string failure)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        var reportHook = failure == "report-deny" ? "return Deny('report blocked');" :
            failure == "report-throw" ? "throw Error('report failed');" : "return AskHuman('Approve report?');";
        await SetPolicy(ctx, caller, "export default {id:'admission',title:'Admission',before(a){if(a.type==='report.commit'){" +
            reportHook + "}return Allow();}};");
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        var draft = failure == "checkpoints"
            ? new BlockOp.UpsertBlockOp("draft", ReportBlockKind.Checkpoints, null, TestJson.Parse("""{"items":[]}"""))
            : new BlockOp.UpsertBlockOp("draft", ReportBlockKind.Prose, "Proposed prose", null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Record.ExceptionAsync(() => ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [draft]), timeout.Token));
        Assert.NotNull(error);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        var persisted = (await ctx.Store.LoadTrackAggregateAsync(caller.TrackId, default))!;
        Assert.Empty(persisted.OpenAsks);
        Assert.Empty(persisted.Tasks);
        Assert.DoesNotContain(persisted.Report.Blocks, b => b.Id == "draft");
    }

    [Fact]
    public async Task UnclosedTaskFenceCannotAuthorTask()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, "export default {id:'deny',title:'Deny',before:a=>a.type==='task.dispatch'?Deny('dispatch blocked'):Allow()};");
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("fenced", ReportBlockKind.Task,
                "```json\n{\"task_key\":\"fenced\",\"ready\":true,\"access\":\"read_only\",\"goal_or_cmd\":\"Inspect\"}", null)])));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
        Assert.Empty(await ctx.Kernel.ListTasksAsync(caller.TrackId));
        Assert.DoesNotContain(await ctx.Kernel.ListSessionsAsync(caller.TrackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default), s => s.TaskId is not null);
    }

    [Fact]
    public async Task PlainTask_DoesNotValidateInheritedLeadModelAsAnExplicitChoice()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        await ctx.Kernel.Actors.GetOrActivate(snapshot.Track.Id).UpdateSessionAsync(snapshot.Track.LeadSessionId!, new(Model: new("lead-only-model")), new InteropPrincipal.Human(), default);
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5"])];
        var dispatched = await FlowFixture.StartWorkerAsync(ctx.Kernel, snapshot.Track.Id, FlowFixture.Worker("plain", "Inspect"));
        Assert.NotEqual(TaskRunStatus.Failed, dispatched.Task.Status);
        Assert.Contains(await ctx.Kernel.ListSessionsAsync(snapshot.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default), s => s.TaskId == dispatched.Task.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnifiedTaskCreationResolvesRoutingBeforePersistingSession(bool explicitSelection)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5"]),
            new("claude", true, "1", null, ["sonnet"])];
        await SetPolicy(ctx, await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!),
            "export default {id:'route',title:'Route',tasks:{routing:[{when:{},provider:'claude',model:'sonnet'}]}};");
        var receipt = await FlowFixture.StartWorkerAsync(ctx.Kernel, snapshot.Track.Id,
            FlowFixture.Worker("routed-create", "Inspect", provider: explicitSelection ? "codex" : null,
                model: explicitSelection ? "gpt-5" : null));
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var session = (await ctx.Kernel.GetSessionItemsAsync(receipt.Session.Id, new(), new InteropPrincipal.Human(), default)).Session;
        Assert.Equal(explicitSelection ? "codex" : "claude", session.Provider);
        Assert.Equal(explicitSelection ? "gpt-5" : "sonnet", session.Model);
        var stored = (await ctx.Store.LoadTrackAggregateAsync(snapshot.Track.Id, default))!.Sessions.Single(s => s.Id == session.Id);
        Assert.Equal((session.Provider, session.Model, session.PermissionMode), (stored.Provider, stored.Model, stored.PermissionMode));
        var launch = Assert.Single(ctx.AgentRuntime.OpenedSessions, s => s.LaunchSpec.StateKey == session.Id).LaunchSpec;
        Assert.Equal((session.Provider, session.Model), (launch.Provider, launch.Model));
    }

    [Fact]
    public async Task FlowPreservesAnEnforcedProviderAliasInTheSpec()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        ctx.AgentRuntime.Providers = [new("acp:antigravity", true, "1", null, [])];
        await SetPolicy(ctx, await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!),
            "export default {id:'alias',title:'Alias',tasks:{routing:[{when:{},provider:'agy',enforce:true}]}};");

        var created = await FlowFixture.StartWorkerAsync(ctx.Kernel, snapshot.Track.Id,
            FlowFixture.Worker("alias", "Inspect", provider: "agy"));
        Assert.Equal("agy", (await ctx.Kernel.GetTrackFlowAsync(snapshot.Track.Id)).Spec.GetProperty("nodes")[0]
            .GetProperty("do").GetProperty("provider").GetString());
        Assert.Equal("acp:antigravity", SessionLaunchValidator.NormalizeProvider(created.Session.Provider));
    }

    [Fact]
    public async Task RoutedProvider_DoesNotInheritAnotherProvidersLeadModel()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await ctx.Kernel.Actors.GetOrActivate(caller.TrackId).UpdateSessionAsync(caller.SessionId, new(Model: new("lead-only-model")), new InteropPrincipal.Human(), default);
        ctx.AgentRuntime.ProbeScript = _ => Task.FromResult<IReadOnlyList<ProviderStatus>>([
            new("codex", true, "1", null, ["gpt-5"]), new("claude", true, "1", null, ["sonnet"])]);
        await SetPolicy(ctx, caller, "export default {id:'route-provider',title:'Route',tasks:{routing:[{when:{},provider:'claude'}]}};");
        var receipt = await FlowFixture.StartWorkerAsync(ctx.Kernel, caller.TrackId, FlowFixture.Worker("route-provider", "Inspect"));
        Assert.Equal("claude", receipt.Session.Provider);
        Assert.Null(receipt.Session.Model);
        Assert.Null(receipt.Task.Provider);
    }

    [Fact]
    public async Task PreviewAskHuman_ReturnsPromptlyWithoutPersistingAskOrData()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, "export default {id:'ask',title:'Ask',before(a,ctx){if(a.type==='tool.track_set'){ctx.data.changed=true;return AskHuman('Approve?',{approve:'Approve',deny:'Reject'});}return Allow();}};");
        var before = (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.Data.GetRawText();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller,
            KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source = "export default {id:'next',title:'Next'};", dryRun = true }), timeout.Token));
        Assert.Equal(KernelErrorCode.PolicyDenied, error.Code);
        var after = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId);
        Assert.Empty(after.PendingApprovals);
        Assert.Equal(before, after.Track.Data.GetRawText());
    }

    [Theory]
    [InlineData("dryRun")]
    [InlineData("clear")]
    public async Task TemplateBooleanArguments_AreValidatedBeforeGuardEvaluation(string name)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, "export default {id:'bool',title:'Bool',before(a,ctx){if(a.type==='tool.track_set')return Deny('guard ran');return Allow();}};");
        var before = (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.Data.GetRawText();
        var error = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller,
            KernelMcpToolNames.TrackSet, TestJson.FromObject(new Dictionary<string, object> { [name] = "true", ["clear"] = name == "clear" ? "true" : false })));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
        Assert.Equal(before, (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.Data.GetRawText());
    }
    [Fact]
    public async Task SchedulingAsk_UnrelatedReportSyncPreservesApprovalForSameContract()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, "export default {id:'ask',title:'Ask',tasks:{routing:[{when:{},provider:'codex',model:'gpt-5'}]},before:a=>a.type==='task.dispatch'?AskHuman('Approve?',{approve:'Approve',deny:'Reject'}):Allow()};");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await FlowFixture.AddAsync(ctx.Kernel, caller.TrackId, FlowFixture.Worker("routed", "Inspect"));
        while ((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token)).PendingApprovals.Count == 0)
            await Task.Delay(10, timeout.Token);
        var first = Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        await ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("unrelated", ReportBlockKind.Prose, "Sync", null)]));
        await ctx.Kernel.RespondInteractionAsync(first.SessionId, first.Id, new(ExecutionId: first.ExecutionId, OptionId: first.Options.Single(o => o.IsApproval).Id), new InteropPrincipal.Human(), default);
        await ctx.Kernel.WaitForTrackIdleAsync(caller.TrackId, timeout.Token);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        var worker = Assert.Single(await ctx.Kernel.ListSessionsAsync(caller.TrackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default), summary => summary.TaskId is not null).Session;
        Assert.Equal("gpt-5", worker.Model);
    }

    [Fact]
    public async Task SchedulingAsk_CloseThenReopenRequestsFreshApproval()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, "export default {id:'ask',title:'Ask',before:a=>a.type==='task.dispatch'?AskHuman('Approve?',{approve:'Approve',deny:'Reject'}):Allow()};");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await FlowFixture.AddAsync(ctx.Kernel, caller.TrackId, FlowFixture.Worker("pending", "Inspect"));
        while ((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token)).PendingApprovals.Count == 0)
            await Task.Delay(10, timeout.Token);
        var first = Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        var pending = (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.TaskKey == "pending");
        Assert.Equal(TaskRunStatus.Pending, pending.Status);
        await ctx.Kernel.PatchTrackAsync(caller.TrackId, new(Closed: true), timeout.Token);
        await ctx.Kernel.WaitForTrackIdleAsync(caller.TrackId, timeout.Token);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        Assert.Equal(TaskRunStatus.Pending, (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.Id == pending.Id).Status);
        await ctx.Kernel.PatchTrackAsync(caller.TrackId, new(Closed: false), timeout.Token);
        TrackAggregateSnapshot reopened;
        do
        {
            await Task.Delay(10, timeout.Token);
            reopened = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token);
        } while (reopened.PendingApprovals.Count == 0);
        var fresh = Assert.Single(reopened.PendingApprovals);
        Assert.NotEqual(first.Id, fresh.Id);
        await ctx.Kernel.RespondInteractionAsync(fresh.SessionId, fresh.Id, new(ExecutionId: fresh.ExecutionId, OptionId: fresh.Options.Single(o => o.IsApproval).Id), new InteropPrincipal.Human(), timeout.Token);
        await ctx.Kernel.WaitForTrackIdleAsync(caller.TrackId, timeout.Token);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        Assert.Single(await ctx.Kernel.ListSessionsAsync(caller.TrackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default), summary => summary.TaskId == pending.Id);
        Assert.Equal(TaskRunStatus.Running, (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.Id == pending.Id).Status);
    }

    [Fact]
    public async Task UnsupportedRewrite_CannotTurnDryRunIntoMutation()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        await SetPolicy(ctx, caller, "export default {id:'preview',title:'Preview',before:a=>a.type==='tool.track_set'?Rewrite({arguments:{clear:true,dryRun:false}}):Allow()};");
        await Assert.ThrowsAsync<InvalidOperationException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet,
            TestJson.FromObject(new { templateId = "other", dryRun = true })));
        Assert.Equal("preview", (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.TemplateId);
    }

}

// Real policy execution uses a wall-clock sandbox budget. Keep these scenarios
// apart from concurrent test workloads without relaxing production limits.
[CollectionDefinition("Template policy sandbox", DisableParallelization = true)]
public sealed class TemplatePolicySandboxCollection;
