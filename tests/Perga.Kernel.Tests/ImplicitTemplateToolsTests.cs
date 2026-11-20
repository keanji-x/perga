using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.PolicyVm;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class ImplicitTemplateToolsTests
{
    private const string Source = """
        export default defineTemplate({ id: "authored", title: "Authored",
          plugins: ["search", "new-plugin", "search"],
          initialState: inputs => ({ marker: inputs.marker }),
          before: () => Deny("authored policy active") });
        """;

    private static McpSessionPrincipal Caller(TrackAggregateSnapshot snap) =>
        new("token", snap.Track.Id, snap.Track.LeadSessionId!, true, null, null, []);

    private static Task<JsonElement> Call(KernelTestContext ctx, McpSessionPrincipal caller,
        string tool, object args) => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, tool, TestJson.FromObject(args));

    [Fact]
    public async Task Discovery_ListsCompiledManifest_InspectsSourceAndEmbeddedContract()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate(Source);
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snap);
        var listed = await Call(ctx, caller, KernelMcpToolNames.TemplateLs, new { });
        Assert.Equal("authored", Assert.Single(listed.EnumerateArray()).GetProperty("id").GetString());
        var inspected = await Call(ctx, caller, KernelMcpToolNames.TemplateCat, new { templateId = "authored" });
        Assert.Equal(Source, inspected.GetProperty("jsSource").GetString());
        Assert.Equal("Authored", inspected.GetProperty("manifest").GetProperty("title").GetString());
        var contract = await Call(ctx, caller, KernelMcpToolNames.TemplateCat, new { });
        Assert.Contains("defineTemplate", contract.GetProperty("contract").GetString());
        Assert.Equal(contract.GetRawText(), (await Call(ctx, caller, KernelMcpToolNames.TemplateCat,
            new { templateId = "__contract__" })).GetRawText());
        var ex = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, caller, KernelMcpToolNames.TemplateCat,
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
        Assert.Equal("initialized", persisted.Track.TemplateState.GetProperty("marker").GetString());
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
        var denied = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, caller, KernelMcpToolNames.TemplateLs, new { }));
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
              initialState: () => ({ requests: 0 }),
              before: (call, ctx) => {
                ctx.state.requests++;
                return call.arguments.inputs?.suspend
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
            KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source = Source, inputs = new { suspend = true } }), timeout.Token);
        ApprovalDto? ask = null;
        while (ask is null)
        {
            ask = (await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id, timeout.Token)).PendingApprovals.FirstOrDefault();
            if (ask is null) await Task.Delay(10, timeout.Token);
        }
        Assert.False(suspended.IsCompleted);
        Assert.Equal(1, (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!
            .Track.TemplateState.GetProperty("requests").GetInt32());
        await Call(ctx, caller, KernelMcpToolNames.TrackSet, new { source = replacement });
        var events = ctx.Store.PersistedEvents.Count(e => e.Kind == "track.updated");
        await ctx.Kernel.DecideApprovalAsync(snap.Track.Id, ask.ExecutionId, ask.Id,
            ask.Options.Single(option => option.IsApproval).Id, timeout.Token);
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
    public async Task DeniedTrackSet_PersistsGuardState()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await Call(ctx, Caller(snap), KernelMcpToolNames.TrackSet, new { source = """
            export default defineTemplate({ id:'denying', title:'Denying',
              initialState: () => ({ denied: 0 }),
              before: (action, ctx) => { ctx.state.denied++; return Deny('blocked'); } });
            """ });
        var ex = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, Caller(snap),
            KernelMcpToolNames.TrackSet, new { source = Source }));
        Assert.Equal(KernelErrorCode.PolicyDenied, ex.Code);
        var persisted = (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!;
        Assert.Equal(1, persisted.Track.TemplateState.GetProperty("denied").GetInt32());
        Assert.Equal("denying", persisted.Track.TemplateId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"templateId\":\"a\",\"source\":\"b\"}")]
    [InlineData("{\"source\":\"export default defineTemplate({\"}")]
    [InlineData("{\"source\":\"export default defineTemplate({id:'bad',title:'Bad',initialState:()=>{throw new Error('init failed')}});\"}")]
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
    public async Task InvalidReplacement_PreservesExistingPolicyAndGuardState()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        const string original = """
            export default defineTemplate({ id:'original', title:'Original',
              before: (action, ctx) => { ctx.state.calls = (ctx.state.calls || 0) + 1; return Allow(); } });
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
        await Call(ctx, Caller(snap), KernelMcpToolNames.TemplateLs, new { });
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
        var bound = snap.Sessions.Single() with { Id = "bound", IsLead = false, BoundAttemptId = "attempt" };
        await ctx.Store.UpsertSessionAsync(bound, null, default);
        // Seed before actor hydration in a fresh actor.
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snap.Track.Id);
        foreach (var name in new[] { KernelMcpToolNames.TemplateLs, KernelMcpToolNames.TemplateCat, KernelMcpToolNames.TrackSet })
        {
            Assert.Contains(name, KernelMcpToolNames.All);
            Assert.True(KernelMcpCatalog.IsGovernanceOnly(name));
            Assert.Contains(KernelMcpCatalog.GetToolsForSession(null), d => d.Name == name);
            Assert.DoesNotContain(KernelMcpCatalog.GetToolsForSession("attempt"), d => d.Name == name);
            var ex = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, Caller(snap) with { SessionId = bound.Id }, name, new { source = Source }));
            Assert.Equal(KernelErrorCode.Forbidden, ex.Code);
        }
    }

    [Fact]
    public async Task LeadWithoutPolicy_ReceivesImplicitTemplateAuthoringHint()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new SendMessageRequest("hello", "hint"));
        var prompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        Assert.Contains("Templates are optional and implicit", prompt);
        Assert.Contains("perga_template_ls", prompt);
        Assert.Contains("perga_template_cat", prompt);
        Assert.Contains("perga_track_set", prompt);
        Assert.Contains(".perga.js source", prompt);
    }
}
