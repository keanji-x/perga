using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;
using Perga.VcsKit;

namespace Perga.Kernel.Tests;

public sealed class TrackActorAndHydrationTests
{
    [Fact]
    public async Task HydrationMountHook_CallsEnsureMountedBeforeSessionTerminalAndAttempt_InjectingMergedCwdAndOverlays()
    {
        await using var ctx = new KernelTestContext();
        var overlayLayer = new WorkspaceOverlayLayer(
            LowerDirs: ["/cache/lower"],
            UpperDir: "/overlay/upper",
            WorkDir: "/overlay/work",
            TargetPath: "/mounted/merged");

        ctx.DirectDriver.OverrideMountedMergedCwd = "/mounted/merged";
        ctx.DirectDriver.OverlayLayersToInject = [overlayLayer];

        // Seed track directly into store (simulating fresh daemon boot / cold hydration)
        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Area"));
        var rawJson = TestJson.Parse("{}");
        var seededTrack = new TrackDto(
            Id: "trk_cold",
            AreaId: area.Id,
            ParentTrackId: null,
            ParentAttemptId: null,
            Title: "Cold Hydrated Track",
            Cwd: "/stale/unmounted",
            WorkspaceDriver: "direct",
            WorkspaceSpec: rawJson,
            LeadSessionId: "ses_lead_cold",
            TemplateId: null,
            TemplateInputs: rawJson,
            EnabledPlugins: [],
            ClosedAtMs: null,
            PinnedAtMs: null,
            SortOrder: 0,
            CreatedAtMs: 1000,
            UpdatedAtMs: 1000);
        var seededLead = new AgentSessionDto(
            Id: "ses_lead_cold",
            TrackId: "trk_cold",
            IsLead: true,
            Title: "Lead",
            Provider: "codex",
            Model: null,
            NativeThreadId: null,
            Epoch: 1,
            Status: "idle",
            ArchivedAtMs: null,
            CreatedAtMs: 1000,
            UpdatedAtMs: 1000);

        ctx.Store.SeedDirectAggregate(
            seededTrack,
            ctx.ReportEngine.Parse("# Overview\n\nHydration test."),
            tasks: [],
            sessions: [seededLead]);

        var ensureMountedSeenBeforeSessionOpen = false;
        ctx.AgentRuntime.OnBeforeOpenSession = _ =>
        {
            ensureMountedSeenBeforeSessionOpen = !ctx.DirectDriver.EnsureMountedCalls.IsEmpty;
        };

        // 1. Trigger session turn on uncached track -> must run EnsureMountedAsync BEFORE InitializeAsync
        await ctx.SendAndWaitForTurnAsync("ses_lead_cold", new SendSessionMessageRequest("Check mount", "idem-cold-1"));
        Assert.True(ensureMountedSeenBeforeSessionOpen);
        Assert.Single(ctx.DirectDriver.EnsureMountedCalls);

        Assert.True(ctx.AgentRuntime.OpenedSessions.TryDequeue(out var openedSession));
        Assert.Equal("/mounted/merged", openedSession!.LaunchSpec.Cwd);

        // 2. Open terminal -> uses hydrated MountedWorkspace.MergedCwd
        var term = await ctx.Kernel.OpenTerminalAsync("trk_cold", "ses_lead_cold", new OpenTerminalRequest(Title: "Build Shell"));
        Assert.Equal("/mounted/merged", term.Cwd);
        Assert.True(ctx.TerminalClient.EnsureCalls.TryDequeue(out var termEnsure));
        Assert.Equal("/mounted/merged", termEnsure!.Cwd);
    }

    [Fact]
    public async Task TrackActor_SerializesConcurrentCommands_AndEnforcesOptimisticReadAnchor()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        // Fire 15 sequential-anchor commits vs concurrent conflicting commits with the same ReadAnchor
        var initialRead = await ctx.Kernel.ReadReportAsync(trackId, leadId);

        var concurrentCommits = Enumerable.Range(1, 10)
            .Select(i => ctx.Kernel.CommitReportAsync(
                trackId,
                leadId,
                new ReportCommitRequest(
                    Anchor: initialRead.Anchor,
                    Ops: [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, $"Concurrent note {i}", null)])))
            .ToArray();

        var successes = 0;
        var conflicts = 0;
        foreach (var t in concurrentCommits)
        {
            try
            {
                await t;
                successes++;
            }
            catch (ReportConflictException)
            {
                conflicts++;
            }
        }

        // Single-writer actor guarantees exactly 1 succeeds at docRev=0 and the other 9 deterministically get ReportConflictException!
        Assert.Equal(1, successes);
        Assert.Equal(9, conflicts);

        var afterRead = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        Assert.Equal(1, afterRead.DocRev);
    }

    [Fact]
    public async Task IdleEviction_EvictsIdleActorsAfterTimeout_AndRehydratesWithMountOnNextCall()
    {
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            ActorIdleTimeout = TimeSpan.FromMinutes(10),
            ActorEvictionSweepInterval = TimeSpan.Zero
        });

        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        Assert.Equal(1, ctx.Kernel.Actors.ActiveActorCount);

        // Advance 5 minutes (< 10 min) -> not evicted
        ctx.Time.Advance(TimeSpan.FromMinutes(5));
        var evictedEarly = await ctx.Kernel.Actors.EvictIdleActorsAsync();
        Assert.Equal(0, evictedEarly);
        Assert.True(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));

        // Advance another 6 minutes (total 11 min >= 10 min) -> evicted!
        ctx.Time.Advance(TimeSpan.FromMinutes(6));
        var evicted = await ctx.Kernel.Actors.EvictIdleActorsAsync();
        Assert.Equal(1, evicted);
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));

        // A metadata read rehydrates without mounting. A workspace operation mounts on demand.
        var mountsBefore = ctx.DirectDriver.EnsureMountedCalls.Count;
        var rehydrated = await ctx.Kernel.GetTrackAggregateAsync(trackId);
        Assert.Equal(trackId, rehydrated.Track.Id);
        Assert.Equal(mountsBefore, ctx.DirectDriver.EnsureMountedCalls.Count);
        await ctx.Kernel.Actors.GetOrActivate(trackId).EnsureMountedAsync();
        Assert.Equal(mountsBefore + 1, ctx.DirectDriver.EnsureMountedCalls.Count);
    }

    [Fact]
    public async Task CreatePatchAndDeleteTrack_RunsLifecycleHooks_SwitchesLead_AndTearsDownCleanly()
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate(
            """
            export default defineTemplate({
              id: "dev",
              title: "Dev Track",
              workspace: { onCreateCmd: "echo init-ws", onCloseCmd: "echo cleanup-ws" }
            });
            """);

        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Backend", DefaultCwd: "/repo/perga"));
        var created = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            TemplateId: "dev"));

        var trackId = created.Track.Id;
        var initialLeadId = created.Track.LeadSessionId!;
        await ctx.Kernel.Actors.GetOrActivate(trackId).EnsureMountedAsync();

        // WorkspaceCreateCmd executes during preparation.
        Assert.Contains(ctx.CommandRunner.ExecutedCommands, c => c.Command == "echo init-ws" && c.WorkingDirectory == "/repo/perga");

        // Create a free Subagent and switch LeadSessionId to it
        var subagent = (await ctx.Kernel.CreateSessionAsync(trackId, new CreateSessionRequest(Title: "Co-Lead"), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default)).Receipt.Session;
        Assert.False(subagent.IsLead);

        var patched = await ctx.Kernel.PatchTrackAsync(trackId, new PatchTrackRequest(
            Title: "Renamed Dev Track",
            Pinned: true,
            EnabledPlugins: ["market", "barra"],
            LeadSessionId: subagent.Id));

        Assert.Equal("Renamed Dev Track", patched.Title);
        Assert.NotNull(patched.PinnedAtMs);
        Assert.Equal(subagent.Id, patched.LeadSessionId);

        var sessions = (await ctx.Kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray();
        Assert.True(sessions.Single(s => s.Id == subagent.Id).IsLead);
        Assert.False(sessions.Single(s => s.Id == initialLeadId).IsLead);

        // Open a terminal, then delete the track -> kills terminal, runs onCloseCmd, calls TeardownTrackAsync, removes DB record
        var term = await ctx.Kernel.OpenTerminalAsync(trackId, subagent.Id, new OpenTerminalRequest());
        await ctx.Kernel.DeleteTrackAsync(trackId);

        Assert.Contains(term.Id, ctx.TerminalClient.KillCalls);
        Assert.Contains(ctx.CommandRunner.ExecutedCommands, c => c.Command == "echo cleanup-ws");
        Assert.Single(ctx.DirectDriver.TeardownCalls);
        Assert.Null(await ctx.Store.GetTrackAsync(trackId, CancellationToken.None));
    }
}
