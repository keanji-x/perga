using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.PolicyVm;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class ImplicitTemplateToolsTests
{
    [Fact]
    public async Task OneShotTemplateReplacementArmsWhileOldWatchShutsDown()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snap);
        var id = snap.Track.Id;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.CommandRunner.Handler = async (_, ct) =>
        {
            using var registration = ct.Register(() => stopping.TrySetResult());
            started.TrySetResult();
            await release.Task;
            return new ShellCommandResult(0, "", "", false, 1);
        };

        try
        {
            await Call(ctx, caller, KernelMcpToolNames.TrackSet, new { source = """
                export default {id:'old-watch', title:'Old watch', flow:[
                  {id:'pulse', do:{kind:'watch', cmd:'old probe'}}
                ]};
                """ });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var old = Assert.Single(await ctx.Kernel.ListTasksAsync(id));
            var due = ctx.Time.GetUtcNow().AddMinutes(1);
            await Call(ctx, caller, KernelMcpToolNames.TrackSet, new { source = $$$"""
                export default {id:'new-wake', title:'New wake', flow:[
                  {id:'pulse', when:{at:'{{{due:O}}}'}, do:{kind:'wake', session:'lead', prompt:'Run replacement'}}
                ]};
                """ });
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ctx.Time.Advance(TimeSpan.FromMinutes(1));
            Assert.Single(await ctx.Kernel.FireDueTrackSchedulesAsync(due.ToUnixTimeMilliseconds()));
            var row = Assert.Single((await ctx.Store.LoadTrackAggregateAsync(id, default))!.Tasks,
                task => task.TaskKey == "pulse");
            Assert.NotEqual(old.Id, row.Id);
            Assert.Equal(TaskRunStatus.Pending, row.Status);
            Assert.True(row.Ready);
            Assert.Equal(0, row.RunCount);
            var clock = (await ctx.Store.GetAsync(id, "flow:pulse", default))!;
            Assert.Equal(TrackScheduleStates.Done, clock.State);
            Assert.Equal(1, clock.FiredCount);
            Assert.Empty(ctx.AgentRuntime.OpenedSessions);
        }
        finally { release.TrySetResult(); }

        await ctx.Kernel.WaitForTrackIdleAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = Assert.Single(await ctx.Kernel.ListTasksAsync(id), task => task.TaskKey == "pulse");
        Assert.Equal(TaskRunStatus.Done, replacement.Status);
        Assert.Equal(1, replacement.RunCount);
        Assert.Contains("Run replacement", Assert.Single(Assert.Single(ctx.AgentRuntime.OpenedSessions).RecordedInputs).Prompt);
        Assert.Empty(await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task ImmediateWakeRunsWhenItsStartupEventCannotBePublished()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var failed = false;
        ctx.Store.BeforeEventPublication = events =>
        {
            if (!failed && events.Any(evt => evt.Kind == KernelEventKinds.TaskUpdated && evt.TrackId == snap.Track.Id))
            {
                failed = true;
                throw new IOException("Failed to read the wake startup event.");
            }
            return Task.CompletedTask;
        };
        await Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new { source = """
            export default {id:'wake-seed', title:'Wake seed',
              flow:[{id:'seed', do:{kind:'wake', session:'lead', prompt:'Run immediate wake'}}]};
            """ });
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(failed);
        Assert.Contains("Run immediate wake", Assert.Single(Assert.Single(ctx.AgentRuntime.OpenedSessions).RecordedInputs).Prompt);
        Assert.Equal(TaskRunStatus.Done, Assert.Single(await ctx.Kernel.ListTasksAsync(snap.Track.Id)).Status);
        Assert.Equal(0, ctx.Kernel.DispatchAdmission.ActiveDispatches);
    }

    [Fact]
    public async Task TemplateDetachReleasesResponsiveRunWhenSiblingShutdownTimesOut()
    {
        await using var ctx = new KernelTestContext(new KernelOptions { SessionStartupTimeout = TimeSpan.FromMilliseconds(100) },
            policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.CommandRunner.Handler = async (command, ct) =>
        {
            if (command.Command == "slow")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            else
            {
                secondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return new ShellCommandResult(0, "", "", false, 0);
        };
        await Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new { source = """
            export default {id:'two-runs', title:'Two runs', flow:[
              {id:'first', do:{kind:'run', cmd:'slow', access:'read_only'}},
              {id:'second', do:{kind:'run', cmd:'responsive', access:'read_only'}}]};
            """ });
        var id = snap.Track.Id;
        try
        {
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new { clear = true });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                var projection = (await ctx.Kernel.GetTrackFlowAsync(id, timeout.Token)).Projection;
                if (projection.Archived.Any(task => task.Id == "second"))
                {
                    Assert.Equal("first", Assert.Single(projection.Nodes).Id);
                    Assert.Equal(1, ctx.Kernel.DispatchAdmission.ActiveDispatches);
                    break;
                }
                await Task.Delay(10, timeout.Token);
            }
        }
        finally { releaseFirst.TrySetResult(); }
        await ctx.Kernel.CancelTaskAsync(id, null, "first").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, ctx.Kernel.DispatchAdmission.ActiveDispatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedTemplateSchedulesItsSeedWhenResponseCannotBePublished(bool cancelRequest)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snap);
        using var request = new CancellationTokenSource();
        var publicationFailed = false;
        if (cancelRequest)
            ctx.Store.AfterTemplateCommit = request.Cancel;
        else
            ctx.Store.BeforeEventPublication = events =>
            {
                if (events.Any(evt => evt.Kind == "track.updated" && evt.TrackId == snap.Track.Id))
                {
                    ctx.Store.BeforeEventPublication = null;
                    publicationFailed = true;
                    throw new IOException("Failed to read the committed template event.");
                }
                return Task.CompletedTask;
            };

        var binding = KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet,
            TestJson.FromObject(new { source = """
                export default {id:'seed-command', title:'Seed command',
                  flow:[{id:'seed', when:{}, do:{kind:'run', cmd:'seed'}}]};
                """ }), request.Token);
        if (cancelRequest)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => binding);
            // The request can return before the actor finishes the already committed operation.
            await ctx.Kernel.Actors.GetOrActivate(snap.Track.Id).EnqueueAsync(_ => Task.CompletedTask);
        }
        else
        {
            await binding;
            Assert.True(publicationFailed);
        }
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("seed", Assert.Single(ctx.CommandRunner.ExecutedCommands).Command);
        Assert.Equal(TaskRunStatus.Done, Assert.Single(await ctx.Kernel.ListTasksAsync(snap.Track.Id)).Status);
    }

    private const string Source = """
        export default defineTemplate({ id: "authored", title: "Authored",
          plugins: ["search", "new-plugin", "search"],
          inputs: { marker: { type: "string", optional: true } },
          data: { initial: inputs => ({ marker: inputs.marker ?? "default" }) },
          before: () => Deny("authored policy active") });
        """;

    private static McpSessionPrincipal Caller(TrackAggregateSnapshot snap) =>
        new("token", snap.Track.Id, snap.Track.LeadSessionId!, true, null, null, []);

    private static Task<JsonElement> Call(KernelTestContext ctx, McpSessionPrincipal caller,
        string tool, object args) => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, tool, TestJson.FromObject(args));

    [Fact]
    public async Task TrackSetPreviewRecognizesProviderAliasInRouting()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.AgentRuntime.Providers = [new("acp:antigravity", true, "1", null, ["gemini"])];
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var result = await Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new
        {
            dryRun = true,
            source = """
                export default defineTemplate({ id: "routed", title: "Routed",
                  tasks: { routing: [{ when: {}, provider: "agy", model: "gemini" }] } });
                """
        });

        Assert.Empty(result.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Discovery_ListsCompiledManifest_InspectsSourceAndEmbeddedContract()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate(Source);
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snap);
        var listed = await Call(ctx, caller, KernelMcpToolNames.TemplateList, new { });
        Assert.Equal("authored", Assert.Single(listed.EnumerateArray()).GetProperty("id").GetString());
        var inspected = await Call(ctx, caller, KernelMcpToolNames.TemplateRead, new { templateId = "authored" });
        Assert.Equal(Source, inspected.GetProperty("jsSource").GetString());
        Assert.Equal("Authored", inspected.GetProperty("manifest").GetProperty("title").GetString());
        var contract = await Call(ctx, caller, KernelMcpToolNames.TemplateRead, new { });
        Assert.Contains("defineTemplate", contract.GetProperty("contract").GetString());
        Assert.Equal(contract.GetRawText(), (await Call(ctx, caller, KernelMcpToolNames.TemplateRead,
            new { templateId = "__contract__" })).GetRawText());
        var ex = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, caller, KernelMcpToolNames.TemplateRead,
            new { templateId = "missing" }));
        Assert.Equal(KernelErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Contract_PrefersConfiguredFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "perga-template.d.ts"), "configured contract");
            await using var ctx = new KernelTestContext(new KernelOptions { TemplatesDirectory = directory });
            Assert.Equal("configured contract", ctx.Kernel.InspectTemplate(null).GetProperty("contract").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Apply_InitializesMergesPersistsAndRehydratesPolicy(bool byId)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate(Source);
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.Kernel.PatchTrackAsync(snap.Track.Id, new PatchTrackRequest(EnabledPlugins: ["existing", "search"]));
        var caller = Caller(snap);
        var registration = ctx.Kernel.McpSessionBridge.RegisterSession(snap.Track.Id,
            caller.SessionId, true, null, null, ["existing", "search"]);
        var result = await Call(ctx, caller, KernelMcpToolNames.TrackSet, byId
            ? new { templateId = "authored", inputs = new { marker = "initialized" } }
            : (object)new { source = Source, inputs = new { marker = "initialized" } });
        Assert.Equal("authored", result.GetProperty("templateId").GetString());
        var persisted = (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!;
        Assert.Equal(Source, persisted.TemplateSnapshotSource);
        Assert.Equal("initialized", persisted.Track.TemplateInputs.GetProperty("marker").GetString());
        Assert.Equal("initialized", persisted.Track.Data.GetProperty("marker").GetString());
        Assert.Equal(["existing", "search", "new-plugin"], persisted.Track.EnabledPlugins);
        Assert.Equal(persisted.Track.EnabledPlugins,
            ctx.Kernel.McpSessionBridge.ResolveToken(registration.Principal.Token)!.EnabledPlugins);
        Assert.Contains(ctx.Store.PersistedEvents, e => e.Kind == "track.updated" &&
            e.Payload.TryGetProperty("templateId", out var id) && id.GetString() == "authored");
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snap.Track.Id);
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(snap.Track.Id));
        var reloaded = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        Assert.Equal(Source, reloaded.TemplateSnapshotSource);
        var denied = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, caller, KernelMcpToolNames.TemplateList, new { }));
        Assert.Equal(KernelErrorCode.PolicyDenied, denied.Code);
    }

    [Fact]
    public async Task AskHumanApproval_AfterPolicySwap_RejectsStaleTrackSet()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snap);
        const string asking = """
            export default defineTemplate({ id:'asking', title:'Asking',
              data: { initial: { requests: 0 } },
              before: (call, ctx) => {
                ctx.data.requests++;
                return call.arguments.inputs?.marker === 'suspend'
                  ? AskHuman('Approve replacement?', { approve: 'Approve', deny: 'Reject' }) : Allow();
              } });
            """;
        const string replacement = """
            export default defineTemplate({ id:'replacement', title:'Replacement',
              before: () => Deny('Replacement policy blocks calls') });
            """;
        await Call(ctx, caller, KernelMcpToolNames.TrackSet, new { source = asking });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var suspended = KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller,
            KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source = Source, inputs = new { marker = "suspend" } }), timeout.Token);
        ApprovalDto? ask = null;
        while (ask is null)
        {
            ask = (await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id, timeout.Token)).PendingApprovals.FirstOrDefault();
            if (ask is null) await Task.Delay(10, timeout.Token);
        }
        Assert.False(suspended.IsCompleted);
        Assert.Equal(0, (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!
            .Track.Data.GetProperty("requests").GetInt32());
        await Call(ctx, caller, KernelMcpToolNames.TrackSet, new { source = replacement });
        var events = ctx.Store.PersistedEvents.Count(e => e.Kind == "track.updated");
        await ctx.Kernel.RespondInteractionAsync(ask.SessionId, ask.Id, new(ExecutionId: ask.ExecutionId, OptionId: ask.Options.Single(option => option.IsApproval).Id), new InteropPrincipal.Human(), timeout.Token);
        var ex = await Assert.ThrowsAsync<KernelException>(() => suspended);
        Assert.Equal(KernelErrorCode.PolicyDenied, ex.Code);
        var persisted = (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!;
        Assert.Equal("replacement", persisted.Track.TemplateId);
        Assert.Equal(replacement, persisted.TemplateSnapshotSource);
        Assert.Equal(events, ctx.Store.PersistedEvents.Count(e => e.Kind == "track.updated"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentTrackSet_RejectsEarlierAuthorization_EvenWhenReapplyingSamePolicy(bool alreadyTemplated)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        const string allowing = """
            export default defineTemplate({ id:'allowing', title:'Allowing', before: () => Allow() });
            """;
        var caller = Caller(snap);
        if (alreadyTemplated)
            await Call(ctx, caller, KernelMcpToolNames.TrackSet, new { source = allowing });
        var args = TestJson.FromObject(new { source = allowing });
        var generations = await Task.WhenAll(
            ctx.Kernel.GuardMcpToolCallAsync(snap.Track.Id, caller.SessionId, "perga", KernelMcpToolNames.TrackSet, args),
            ctx.Kernel.GuardMcpToolCallAsync(snap.Track.Id, caller.SessionId, "perga", KernelMcpToolNames.TrackSet, args));
        Assert.Equal(generations[0], generations[1]);
        await ctx.Kernel.SetTrackTemplateAsync(snap.Track.Id, caller.SessionId, null, allowing, generations[0]);
        var before = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        var events = ctx.Store.PersistedEvents.Count;
        var ex = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.SetTrackTemplateAsync(
            snap.Track.Id, caller.SessionId, null, allowing, generations[1]));
        Assert.Equal(KernelErrorCode.PolicyDenied, ex.Code);
        Assert.Equal(before.Track, (await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id)).Track);
        Assert.Equal(events, ctx.Store.PersistedEvents.Count);
    }

    [Fact]
    public async Task TrackSet_AfterActorEviction_RejectsAuthorizationFromBeforePolicySwap()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snap);
        var args = TestJson.FromObject(new { source = Source });
        var generation = await ctx.Kernel.GuardMcpToolCallAsync(snap.Track.Id, caller.SessionId,
            "perga", KernelMcpToolNames.TrackSet, args);
        await Call(ctx, caller, KernelMcpToolNames.TrackSet, new { source = Source });

        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snap.Track.Id);
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(snap.Track.Id));
        var denied = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.GuardMcpToolCallAsync(
            snap.Track.Id, caller.SessionId, "perga", KernelMcpToolNames.TrackSet, args));
        Assert.Equal(KernelErrorCode.PolicyDenied, denied.Code);

        var before = (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!;
        var events = ctx.Store.PersistedEvents.Count;
        var stale = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.SetTrackTemplateAsync(
            snap.Track.Id, caller.SessionId, null, Source, generation));
        Assert.Equal(KernelErrorCode.PolicyDenied, stale.Code);
        var after = (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!;
        Assert.Equal(before.Track, after.Track);
        Assert.Equal(before.TemplateSnapshotSource, after.TemplateSnapshotSource);
        Assert.Equal(events, ctx.Store.PersistedEvents.Count);
    }

    [Fact]
    public async Task DeniedTrackSet_DiscardsGuardMutations()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new { source = """
            export default defineTemplate({ id:'denying', title:'Denying',
              data: { initial: { denied: 0 } },
              before: (action, ctx) => { ctx.data.denied++; return Deny('blocked'); } });
            """ });
        var ex = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, Caller(snap),
            KernelMcpToolNames.TrackSet, new { source = Source }));
        Assert.Equal(KernelErrorCode.PolicyDenied, ex.Code);
        var persisted = (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!;
        Assert.Equal(0, persisted.Track.Data.GetProperty("denied").GetInt32());
        Assert.Equal("denying", persisted.Track.TemplateId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"templateId\":\"a\",\"source\":\"b\"}")]
    [InlineData("{\"source\":\"export default defineTemplate({\"}")]
    [InlineData("{\"source\":\"export default defineTemplate({id:'bad',title:'Bad',data:{initial:()=>{throw new Error('init failed')}}});\"}")]
    [InlineData("{\"source\":\"x\",\"inputs\":[]}")]
    [InlineData("{\"source\":null}")]
    public async Task InvalidApplication_DoesNotMutateTrackOrSnapshotOrEmit(string arguments)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var before = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        var events = ctx.Store.PersistedEvents.Count;
        var ex = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(
            ctx.Kernel, Caller(snap), KernelMcpToolNames.TrackSet, TestJson.Parse(arguments)));
        Assert.Equal(KernelErrorCode.InvalidArgument, ex.Code);
        var after = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        Assert.Equal(before.Track, after.Track);
        Assert.Equal(before.TemplateSnapshotSource, after.TemplateSnapshotSource);
        Assert.Equal(events, ctx.Store.PersistedEvents.Count);
    }

    [Fact]
    public async Task InvalidReplacement_PreservesExistingPolicyAndData()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        const string original = """
            export default defineTemplate({ id:'original', title:'Original',
              before: (action, ctx) => { ctx.data.calls = (ctx.data.calls || 0) + 1; return Allow(); } });
            """;
        await Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new { source = original });
        var before = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        var events = ctx.Store.PersistedEvents.Count;
        var ex = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new { source = "invalid JS !!!" }));
        Assert.Equal(KernelErrorCode.InvalidArgument, ex.Code);
        var after = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        Assert.Equal(before.Track, after.Track);
        Assert.Equal(original, after.TemplateSnapshotSource);
        Assert.Equal(events, ctx.Store.PersistedEvents.Count);
    }

    [Fact]
    public async Task FailedPersistence_DoesNotAdoptPolicy_AndRetrySucceeds()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var events = ctx.Store.PersistedEvents.Count;
        ctx.Store.FailTemplateUpdate = true;
        await Assert.ThrowsAsync<IOException>(() => Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet,
            new { source = Source, inputs = new { marker = "retry" } }));
        var unchanged = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        Assert.Equal(snap.Track, unchanged.Track);
        Assert.Null(unchanged.TemplateSnapshotSource);
        Assert.Equal(events, ctx.Store.PersistedEvents.Count);
        // This would be denied if the candidate policy had been adopted prematurely.
        await Call(ctx, Caller(snap), KernelMcpToolNames.TemplateList, new { });
        ctx.Store.FailTemplateUpdate = false;
        await Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new { source = Source, inputs = new { marker = "retry" } });
        Assert.Equal("authored", (await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id)).Track.TemplateId);
    }

    [Fact]
    public async Task ClosedTrack_RejectsApplication()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.Kernel.CloseTrackAsync(snap.Track.Id, snap.Track.LeadSessionId!);
        var ex = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new { source = Source }));
        Assert.Equal(KernelErrorCode.Conflict, ex.Code);
        Assert.Null((await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id)).Track.TemplateId);
    }

    [Fact]
    public async Task Tools_AreGovernanceOnly_AndBoundSessionsCannotInvokeThem()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var bound = snap.Sessions.Single() with { Id = "bound", IsLead = false };
        var owningTask = new TaskDto("tsk_bound", snap.Track.Id, "b_1", "bound-task", 1, "agent", null, AccessMode.ReadOnly,
            true, [], "Work", null, null, null, TaskRunStatus.Running, null, SemanticVerdict.None, null, 1, 1,
            SessionId: bound.Id, RunCount: 1, StartedAtMs: 1);
        await ctx.Store.UpsertTaskAsync(owningTask, null, default, session: bound);
        // Seed before actor hydration in a fresh actor.
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snap.Track.Id);
        foreach (var name in new[]
        {
            KernelMcpToolNames.TemplateList, KernelMcpToolNames.TemplateRead,
            KernelMcpToolNames.TrackSet, KernelMcpToolNames.TemplateUpdateInputs
        })
        {
            Assert.Contains(name, KernelMcpToolNames.All);
            Assert.True(KernelMcpCatalog.IsGovernanceOnly(name));
            Assert.Contains(KernelMcpCatalog.GetToolsForSession(null, isLead: true), d => d.Name == name);
            Assert.Equal(name is KernelMcpToolNames.TemplateList or KernelMcpToolNames.TemplateRead,
                KernelMcpCatalog.GetToolsForSession(null).Any(d => d.Name == name));
            Assert.DoesNotContain(KernelMcpCatalog.GetToolsForSession("tsk_bound"), d => d.Name == name);
            var ex = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, Caller(snap) with { SessionId = bound.Id, BoundTaskId = "tsk_bound", TaskRunCount = 1 }, name, new { source = Source }));
            Assert.Equal(KernelErrorCode.Forbidden, ex.Code);
        }
    }

    [Fact]
    public async Task LeadWithoutPolicy_ReceivesImplicitTemplateAuthoringHint()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new SendSessionMessageRequest("hello", "hint"));
        var prompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        Assert.Contains("Templates are optional and implicit", prompt);
        Assert.Contains("template_list", prompt);
        Assert.Contains("template_read", prompt);
        Assert.Contains("track_set", prompt);
        Assert.Contains(".perga.js source", prompt);
    }
}
