using System.Runtime.CompilerServices;
using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;
using Perga.VcsKit;

namespace Perga.Kernel.Tests;

public sealed class TrackFlowTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, int timeoutMs = 4000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(15);
        }

        Assert.True(await predicate(), "Timed out waiting for expected condition.");
    }

    private sealed class FakeGitHubForgeService : IGitHubForgeService
    {
        public Func<string, int, PrChecksStatus>? OnGetPrChecks { get; set; }

        public Task<PrChecksStatus> GetPrChecksAsync(string repoOrPath, int prNumber, CancellationToken ct) =>
            Task.FromResult(OnGetPrChecks?.Invoke(repoOrPath, prNumber)
                ?? new PrChecksStatus(prNumber, "sha_default", true, true, [new CheckRunSummary("build", "completed", "success", null)]));

        public Task<PrInfo> PublishPrAsync(PrPublishRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<CiFailureEvidence> GetCiFailureEvidenceAsync(string repoOrPath, int prNumber, string headSha, CancellationToken ct) => throw new NotImplementedException();
        public Task MergePrAsync(string repoOrPath, int prNumber, string expectedHeadSha, CancellationToken ct) => throw new NotImplementedException();
        public Task<IssueInfo> ViewIssueAsync(string repoOrPath, int issueNumber, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class ControllableTimeProvider : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ControllableTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock) return _now;
        }

        public void Advance(TimeSpan delta)
        {
            List<ControllableTimer> toFire = [];
            lock (_lock)
            {
                _now = _now.Add(delta);
                foreach (var t in _timers.ToArray())
                {
                    if (!t.Disposed && t.DueTime <= _now)
                    {
                        toFire.Add(t);
                        _timers.Remove(t);
                    }
                }
            }

            foreach (var t in toFire)
            {
                t.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControllableTimer(this, callback, state, GetUtcNow().Add(dueTime));
            lock (_lock) _timers.Add(timer);
            return timer;
        }

        private sealed class ControllableTimer(ControllableTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset dueTime) : ITimer
        {
            public DateTimeOffset DueTime { get; private set; } = dueTime;
            public bool Disposed { get; private set; }

            public void Fire()
            {
                if (!Disposed) callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._lock)
                {
                    if (Disposed) return false;
                    DueTime = owner._now.Add(dueTime);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._lock)
                {
                    Disposed = true;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public void FlowParser_RejectsUnknownDependsOnWithActionableSuggestion()
    {
        const string json = """
            {
              "rev": 0,
              "nodes": [
                {
                  "id": "step-2",
                  "dependsOn": ["step-1"],
                  "do": { "kind": "run", "prompt": "hi" }
                }
              ]
            }
            """;

        var (spec, errors) = TrackFlowParser.Parse(json);
        Assert.Null(spec);
        var err = Assert.Single(errors);
        Assert.Equal("$.nodes[0].dependsOn", err.Path);
        Assert.Contains("\"when\"", err.Suggestion ?? string.Empty);
        Assert.Contains("\"after\"", err.Suggestion ?? string.Empty);
    }

    [Fact]
    public async Task FlowValidator_RejectsCycles_MissingTargets_InvalidAt_AndActiveNodeDeletion()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var flowSnap = await ctx.Kernel.GetTrackFlowAsync(trackId);
        var flowPath = flowSnap.FilePath!;

        // 1. Cycle + missing target + invalid ISO `at`
        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                { "id": "a", "when": { "after": ["b"] }, "do": { "kind": "run", "cmd": "echo a" } },
                { "id": "b", "when": { "after": ["a", "missing"] }, "do": { "kind": "run", "cmd": "echo b" } },
                { "id": "c", "when": { "at": "not-a-timestamp" }, "do": { "kind": "run", "cmd": "echo c" } }
              ]
            }
            """);

        var bad = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human);
        Assert.False(bad.Ok);
        Assert.Contains(bad.Diagnostics, d => d.Message.Contains("Cycle detected", StringComparison.Ordinal) && d.Message.Contains("a -> b -> a", StringComparison.Ordinal));
        Assert.Contains(bad.Diagnostics, d => d.Message.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(bad.Diagnostics, d => d.Path.Contains(".at", StringComparison.Ordinal));

        // 2. Commit an active cron node & a long-running node, then attempt silent deletion -> MUST reject!
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.CommandRunner.Handler = async (req, ct) =>
        {
            await hold.Task.WaitAsync(ct);
            return new ShellCommandResult(0, "ok", "", false, 10);
        };

        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                { "id": "active-job", "title": "Running", "do": { "kind": "run", "cmd": "sleep 60" } },
                { "id": "nightly", "when": { "cron": "0 9 * * *", "tz": "UTC" }, "do": { "kind": "run", "cmd": "echo hi" } }
              ]
            }
            """);
        var committed = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human);
        Assert.True(committed.Ok);
        Assert.Equal(1, committed.FlowRev);
        Assert.Equal(["active-job", "nightly"], committed.Plan.Added);

        // Try deleting both active nodes silently -> rejected with actionable suggestion!
        await File.WriteAllTextAsync(flowPath, """{"rev": 1, "nodes": []}""");
        var deleteAttempt = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 1, Human);
        Assert.False(deleteAttempt.Ok);
        Assert.Equal(2, deleteAttempt.Diagnostics.Count);
        Assert.All(deleteAttempt.Diagnostics, d => Assert.Contains("\"canceled\": true", d.Suggestion ?? string.Empty));

        // Try mutating `do` on running one-shot node -> rejected!
        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 1,
              "nodes": [
                { "id": "active-job", "title": "Running", "do": { "kind": "run", "cmd": "echo mutated" } },
                { "id": "nightly", "when": { "cron": "0 9 * * *", "tz": "UTC" }, "do": { "kind": "run", "cmd": "echo hi" } }
              ]
            }
            """);
        var mutateAttempt = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 1, Human);
        Assert.False(mutateAttempt.Ok);
        Assert.Contains(mutateAttempt.Diagnostics, d => d.Message.Contains("Cannot mutate", StringComparison.Ordinal));

        // Explicit `canceled: true` via CancelTrackFlowNodeAsync succeeds!
        var canceled = await ctx.Kernel.CancelTrackFlowNodeAsync(trackId, "active-job", Human);
        Assert.True(canceled.Ok);
        Assert.Equal(2, canceled.FlowRev);
        Assert.Equal(["active-job"], canceled.Plan.Canceled);

        hold.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
    }

    [Fact]
    public async Task BackgroundGc_ArchivesTerminalOrphanNodes_AndNeverOverwritesDirtyDraft()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var flowPath = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;

        var releaseCmd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.CommandRunner.Handler = async (_, ct) =>
        {
            await releaseCmd.Task.WaitAsync(ct);
            return new ShellCommandResult(0, "all green", "", false, 5);
        };

        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                { "id": "quick-check", "title": "Quick Check", "do": { "kind": "run", "cmd": "echo green" } }
              ]
            }
            """);
        var res = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human);
        Assert.True(res.Ok);

        // Simulate Lead editing a dirty draft on disk BEFORE the task settles!
        const string dirtyDraft = """
            {
              "rev": 1,
              "nodes": [
                { "id": "quick-check", "title": "Quick Check", "do": { "kind": "run", "cmd": "echo green" } },
                { "id": "draft-node", "title": "Unsaved Draft", "do": { "kind": "run", "cmd": "echo draft" } }
              ]
            }
            """;
        await File.WriteAllTextAsync(flowPath, dirtyDraft);

        // GC waits for a draft to be committed before archiving the settled node.
        releaseCmd.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var diskAfterGc = await File.ReadAllTextAsync(flowPath);
        Assert.Equal(dirtyDraft, diskAfterGc);

        var flowAfterGc = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Equal("done", Assert.Single(flowAfterGc.Projection.Nodes).Status);
        Assert.Empty(flowAfterGc.Projection.Archived);

        // Now test clean disk GC: commit another node without dirtying disk -> disk flow.json gets cleaned too!
        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 1,
              "nodes": [
                { "id": "clean-check", "title": "Clean", "do": { "kind": "run", "cmd": "echo clean" } }
              ]
            }
            """);
        var res2 = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 1, Human);
        Assert.True(res2.Ok);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var archive = (await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Archived;
        var archived = Assert.Single(archive, node => node.Id == "quick-check");
        Assert.Equal("ok", archived.Outcome);
        Assert.Equal("all green", archived.OutcomeSummary);
        var cleanDisk = await File.ReadAllTextAsync(flowPath);
        var (parsedClean, parseErrs) = TrackFlowParser.Parse(cleanDisk);
        Assert.Empty(parseErrs);
        Assert.NotNull(parsedClean);
        Assert.Empty(parsedClean!.Nodes);
    }

    [Fact]
    public async Task FlowOutcomeRouting_AndMultiWaveCronPipeline_AreWaveIdempotent()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var flowPath = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;

        var wave = 0;
        var triageRuns = 0;
        ctx.CommandRunner.Handler = (req, _) =>
        {
            if (req.Command.Contains("metrics", StringComparison.Ordinal))
            {
                var currentWave = Interlocked.Increment(ref wave);
                // Wave 1: breach (exit 1 -> attention); Wave 2: healthy (exit 0 -> ok); Wave 3: breach (exit 2 -> attention)
                return Task.FromResult(currentWave switch
                {
                    1 => new ShellCommandResult(1, "", "p95=900ms breach", false, 5),
                    2 => new ShellCommandResult(0, "p95=120ms ok", "", false, 5),
                    _ => new ShellCommandResult(2, "", "p95=950ms breach2", false, 5)
                });
            }

            if (req.Command.Contains("triage", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref triageRuns);
                return Task.FromResult(new ShellCommandResult(0, "triaged", "", false, 5));
            }

            return Task.FromResult(new ShellCommandResult(0, "", "", false, 5));
        };

        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                {
                  "id": "daily-metrics",
                  "title": "Daily CI Metrics",
                  "when": { "cron": "0 9 * * *", "tz": "UTC" },
                  "do": { "kind": "run", "cmd": "check-metrics" }
                },
                {
                  "id": "prepare",
                  "do": { "kind": "run", "cmd": "prepare" }
                },
                {
                  "id": "ci-triage",
                  "title": "Triage Breach",
                  "when": { "after": [{ "node": "daily-metrics", "on": "attention" }, "prepare"] },
                  "do": { "kind": "run", "cmd": "run-triage" }
                }
              ]
            }
            """);
        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human);
        Assert.True(commit.Ok);

        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        // Fire Wave 1 via schedule store -> attention -> triggers triage #1
        ctx.Time.Advance(TimeSpan.FromMinutes(2));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.Equal(1, wave);
        Assert.Equal(1, triageRuns);

        // Cron-fed pipeline stays resident in flow.json (NOT GC'd)!
        var afterWave1 = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Equal(3, afterWave1.Projection.Nodes.Count);
        Assert.Contains(afterWave1.Projection.Nodes, node => node.Id == "prepare" && node.Status == "done");

        // Fire Wave 2 -> ok -> MUST NOT trigger triage
        ctx.Time.Advance(TimeSpan.FromMinutes(2));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.Equal(2, wave);
        Assert.Equal(1, triageRuns);

        // Fire Wave 3 -> attention -> triggers triage #2
        ctx.Time.Advance(TimeSpan.FromMinutes(2));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.Equal(3, wave);
        Assert.Equal(2, triageRuns);
    }

    [Fact]
    public async Task WatchNode_CmdProbe_Handles75Wait_0Ok_NonZeroAttention_AndTimeoutBlocked()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var flowPath = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;

        var probeAttempts = 0;
        var onOkRan = false;
        ctx.CommandRunner.Handler = (req, _) =>
        {
            if (req.Command == "probe-deploy")
            {
                var attempt = Interlocked.Increment(ref probeAttempts);
                return Task.FromResult(attempt < 2
                    ? new ShellCommandResult(75, "still rolling out", "", false, 5)
                    : new ShellCommandResult(0, "deploy healthy", "", false, 5));
            }

            if (req.Command == "post-deploy")
            {
                onOkRan = true;
                return Task.FromResult(new ShellCommandResult(0, "done", "", false, 5));
            }

            if (req.Command == "probe-timeout")
            {
                return Task.FromResult(new ShellCommandResult(75, "never ready", "", false, 5));
            }

            return Task.FromResult(new ShellCommandResult(1, "", "probe exploded", false, 5));
        };

        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                {
                  "id": "watch-deploy",
                  "title": "Watch Deploy",
                  "do": { "kind": "watch", "cmd": "probe-deploy", "interval": "5s", "timeout": "30s" }
                },
                {
                  "id": "after-deploy",
                  "title": "Post Deploy",
                  "when": { "after": ["watch-deploy"] },
                  "do": { "kind": "run", "cmd": "post-deploy" }
                }
              ]
            }
            """);
        var committed = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human);
        Assert.True(committed.Ok);

        // First probe returned 75 -> node is in "waiting" status with live WatchProgress
        await WaitUntilAsync(async () =>
        {
            var f = await ctx.Kernel.GetTrackFlowAsync(trackId);
            return f.Projection.Nodes.Any(n => n.Id == "watch-deploy" && n.Status == "waiting" && n.WatchProgress?.Detail == "still rolling out");
        });

        // Advance controllable clock by 5s -> second probe returns 0 -> watch settles Ok -> after-deploy runs!
        clock.Advance(TimeSpan.FromSeconds(5));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.True(onOkRan);

        // Now test watch timeout -> settles Blocked and wakes Lead!
        var revAfter = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FlowRev;
        await File.WriteAllTextAsync(flowPath, $$"""
            {
              "rev": {{revAfter}},
              "nodes": [
                {
                  "id": "watch-slow",
                  "title": "Watch Slow",
                  "do": { "kind": "watch", "cmd": "probe-timeout", "interval": "5s", "timeout": "10s" }
                }
              ]
            }
            """);
        var commitTimeout = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: revAfter, Human);
        Assert.True(commitTimeout.Ok);
        await WaitUntilAsync(() => Task.FromResult(ctx.CommandRunner.ExecutedCommands.Any(c => c.Command == "probe-timeout")));
        clock.Advance(TimeSpan.FromSeconds(11));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var finalFlow = await ctx.Kernel.GetTrackFlowAsync(trackId);
        var archivedTimeout = Assert.Single(finalFlow.Projection.Archived, a => a.Id == "watch-slow");
        Assert.Equal("blocked", archivedTimeout.Outcome);
    }

    [Fact]
    public async Task WatchNode_PrChecks_HonorsHeadSha_AndTriggersDownstreamWake()
    {
        var clock = new ControllableTimeProvider();
        var forge = new FakeGitHubForgeService();
        var call = 0;
        forge.OnGetPrChecks = (_, pr) => Interlocked.Increment(ref call) == 1
            ? new PrChecksStatus(pr, "old_sha", true, true, [new CheckRunSummary("ci", "completed", "success", null)])
            : new PrChecksStatus(pr, "new_sha", true, true, [new CheckRunSummary("ci", "completed", "success", null)]);

        await using var ctx = new KernelTestContext(timeProvider: clock, gitHubForgeService: forge);
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var flowPath = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;

        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                {
                  "id": "wait-pr",
                  "title": "Wait PR #115",
                  "do": { "kind": "watch", "prChecks": 115, "headSha": "new_sha", "interval": "5s", "timeout": "30s" }
                },
                {
                  "id": "wake-lead",
                  "title": "Notify Lead",
                  "when": { "after": ["wait-pr"] },
                  "do": { "kind": "wake", "session": "lead", "prompt": "PR #115 is green!" }
                }
              ]
            }
            """);
        var committed = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human);
        Assert.True(committed.Ok);

        await WaitUntilAsync(async () =>
        {
            var f = await ctx.Kernel.GetTrackFlowAsync(trackId);
            return f.Projection.Nodes.Any(n => n.Id == "wait-pr" && n.WatchProgress?.Detail?.Contains("Waiting for headSha", StringComparison.Ordinal) == true);
        });

        clock.Advance(TimeSpan.FromSeconds(5));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        // Lead was woken and both nodes archived cleanly!
        var leadTurns = ctx.Store.GetTurnsForSession(snap.Track.LeadSessionId!);
        Assert.Single(leadTurns);
        var flowAfter = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Empty(flowAfter.Projection.Nodes);
        Assert.Contains(flowAfter.Projection.Archived, a => a.Id == "wait-pr" && a.Outcome == "ok");
        Assert.Contains(flowAfter.Projection.Archived, a => a.Id == "wake-lead" && a.Outcome == "ok");
    }

    [Fact]
    public async Task SessionReuseInFlow_PreservesNativeThreadIdAndEpoch_AndSupportsTaskDoneOutcomes()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var flowPath = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;

        var round1TurnStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRound1Turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerTurns = 0;
        ctx.AgentRuntime.TurnScript = (session, _, ct) =>
        {
            if (session.LaunchSpec.StateKey is { } sid && sid != snap.Track.LeadSessionId && Interlocked.Increment(ref workerTurns) == 1)
            {
                return HoldTurnAsync(sid, round1TurnStarted, releaseRound1Turn.Task, ct);
            }

            return DefaultCompletedTurnAsync();
        };

        // Round 1: create review node -> binds a NativeThreadId on its worker session
        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                {
                  "id": "r1-review",
                  "title": "Review Round 1",
                  "do": { "kind": "run", "role": "review", "access": "read_only", "prompt": "Review PR" }
                }
              ]
            }
            """);
        var c1 = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human);
        Assert.True(c1.Ok);
        var reviewerSessionId = await round1TurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Complete r1-review via real MCP task_done DURING its active turn with outcome="attention" & summary!
        var openedR1 = ctx.AgentRuntime.OpenedSessions.Single(s => s.LaunchSpec.StateKey == reviewerSessionId);
        var mcpHttp = Assert.IsType<McpServerConfig.HttpMcpServer>(openedR1.LaunchSpec.McpServers["perga"]);
        var principal = ctx.Kernel.McpSessionBridge.ResolveToken(mcpHttp.Headers["Authorization"])!;
        Assert.NotNull(principal);
        var doneRes = await ctx.Kernel.InvokeKernelMcpToolAsync(principal, KernelMcpToolNames.TaskDone,
            TestJson.FromObject(new { outcome = "attention", summary = "Found 1 blocker in auth.cs" }));
        Assert.Equal("attention", doneRes.GetProperty("outcome").GetString());
        Assert.Equal("Found 1 blocker in auth.cs", doneRes.GetProperty("outcomeSummary").GetString());
        releaseRound1Turn.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        // Session is now writable (no task_finished lock) and holds its persisted NativeThreadId + Epoch!
        var pageAfterR1 = await ctx.Kernel.GetSessionItemsAsync(reviewerSessionId, new(), Human, default);
        Assert.Null(pageAfterR1.ReadonlyReason);
        var originalThreadId = pageAfterR1.Session.NativeThreadId;
        var originalEpoch = pageAfterR1.Session.Epoch;
        Assert.False(string.IsNullOrWhiteSpace(originalThreadId));

        // An ordinary follow-up may already be running when a new task targets the session.
        var followUpStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFollowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (session, input, ct) => input.Prompt == "Discuss the review"
            ? HoldTurnAsync(session.LaunchSpec.StateKey!, followUpStarted, releaseFollowUp.Task, ct)
            : DefaultCompletedTurnAsync();
        await ctx.Kernel.SendSessionMessageAsync(reviewerSessionId, new(Content: "Discuss the review", RequestKey: "review-follow-up"), Human, default);
        await followUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Round 2: schedule `r2-recheck` reusing `reviewerSessionId` -> preserves NativeThreadId & Epoch!
        var flowRev = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FlowRev;
        await File.WriteAllTextAsync(flowPath, $$"""
            {
              "rev": {{flowRev}},
              "nodes": [
                {
                  "id": "r2-recheck",
                  "title": "Recheck Round 2",
                  "do": { "kind": "run", "session": "{{reviewerSessionId}}", "role": "review", "access": "read_only", "prompt": "Recheck fix" }
                }
              ]
            }
            """);
        var c2 = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: flowRev, Human);
        Assert.True(c2.Ok);
        Assert.Equal("pending", Assert.Single(c2.Flow.Nodes).Status);
        releaseFollowUp.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.Equal("running", Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes).Status);

        var reviewerSessionAfter = (await ctx.Kernel.GetSessionItemsAsync(reviewerSessionId, new(), Human, default)).Session;
        Assert.Equal(originalThreadId, reviewerSessionAfter.NativeThreadId);
        Assert.Equal(originalEpoch, reviewerSessionAfter.Epoch);

        // Verify provider reopened with ResumeNativeThreadId == originalThreadId
        var lastOpened = ctx.AgentRuntime.OpenedSessions.Last(s => s.LaunchSpec.StateKey == reviewerSessionId);
        Assert.Equal(originalThreadId, lastOpened.LaunchSpec.ResumeNativeThreadId);
    }

    [Fact]
    public async Task ReportSkeletonLock_AndTrackSetPathDryRun_EnforceTemplateGuardrails()
    {
        var realVm = new Perga.PolicyVm.PolicyVm();
        await using var ctx = new KernelTestContext(policyVm: realVm);
        var tempPkgDir = Path.Combine(Path.GetTempPath(), $"perga-tpl-pkg-{Guid.NewGuid():N}");
        var badPkgDir = Path.Combine(Path.GetTempPath(), $"perga-tpl-bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPkgDir);
        Directory.CreateDirectory(badPkgDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempPkgDir, "report.md"), """
                # Target

                Initial target text.

                # Progress

                Before the widget.

                ```perga-block widget
                {"use":"builtin:list","view":"itemsView"}
                ```
                """);
            await File.WriteAllTextAsync(Path.Combine(tempPkgDir, "template.perga.js"), """
                export default defineTemplate({
                  id: "local-pkg",
                  title: "Local Package",
                  data: { initial: () => ({ items: [] }) },
                  report: {
                    template: "./report.md",
                    views: {
                      itemsView: (data) => ({ items: data.items || [] })
                    }
                  }
                });
                """);

            await File.WriteAllTextAsync(Path.Combine(badPkgDir, "template.perga.js"), """
                export default defineTemplate({
                  id: "bad-local-pkg",
                  title: "Bad Local",
                  report: {
                    initialMarkdown: "```perga-block widget\n{\"use\":\"builtin:table\",\"view\":\"badTable\"}\n```",
                    views: {
                      badTable: () => ({ notMetrics: [] })
                    }
                  }
                });
                """);

            var (_, snap) = await ctx.CreateDefaultTrackAsync();
            var trackId = snap.Track.Id;
            var leadId = snap.Track.LeadSessionId!;
            var leadReg = ctx.Kernel.McpSessionBridge.RegisterSession(trackId, leadId, isLead: true, null, null, []);

            // 1. track_set(path, dryRun: true) rejects broken view template at compile time!
            var badEx = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.InvokeKernelMcpToolAsync(leadReg.Principal, KernelMcpToolNames.TrackSet,
                    TestJson.FromObject(new { path = badPkgDir, dryRun = true })));
            Assert.Equal(KernelErrorCode.InvalidArgument, badEx.Code);
            Assert.Contains("metrics", badEx.Message, StringComparison.OrdinalIgnoreCase);

            // 2. track_set(path, dryRun: true) previews valid directory without mutating Track!
            var preview = await ctx.Kernel.InvokeKernelMcpToolAsync(leadReg.Principal, KernelMcpToolNames.TrackSet,
                TestJson.FromObject(new { path = tempPkgDir, dryRun = true }));
            Assert.True(preview.GetProperty("dryRun").GetBoolean());
            Assert.Equal("local-pkg", preview.GetProperty("templateId").GetString());
            Assert.Null((await ctx.Kernel.GetTrackAggregateAsync(trackId)).Track.TemplateId);

            // 3. Create Track with local-pkg registered (so InitialReportMarkdown populates report with widget block)
            ctx.Kernel.Templates.RegisterTemplate(Perga.PolicyVm.TemplateBundle.PackDirectory(tempPkgDir));
            var (_, tplSnap) = await ctx.CreateDefaultTrackAsync(templateId: "local-pkg");
            var tplTrackId = tplSnap.Track.Id;
            var tplLeadId = tplSnap.Track.LeadSessionId!;
            var report = await ctx.Kernel.ReadReportAsync(tplTrackId, tplLeadId);
            var widgetBlock = Assert.Single(report.Blocks, b => b.Kind == ReportBlockKind.Widget);

            // 4. AI trying to delete template widget -> PolicyDenied!
            var delEx = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.CommitReportAsync(tplTrackId, tplLeadId,
                    new ReportCommitRequest(report.Anchor, [new BlockOp.DeleteBlockOp(widgetBlock.Id)])));
            Assert.Equal(KernelErrorCode.PolicyDenied, delEx.Code);

            // 5. AI trying to move template widget -> PolicyDenied!
            var moveEx = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.CommitReportAsync(tplTrackId, tplLeadId,
                    new ReportCommitRequest(report.Anchor, [new BlockOp.MoveBlockOp(widgetBlock.Id, 0)])));
            Assert.Equal(KernelErrorCode.PolicyDenied, moveEx.Code);

            var movedWithinSection = $$"""
                <!-- perga:{{widgetBlock.Id}} -->
                ```perga-block widget
                {"use":"builtin:list","view":"itemsView"}
                ```

                Before the widget.
                """;
            var sectionMove = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.CommitReportAsync(tplTrackId, tplLeadId,
                    new ReportCommitRequest(report.Anchor, [new BlockOp.ReplaceSectionOp("Progress", movedWithinSection)])));
            Assert.Equal(KernelErrorCode.PolicyDenied, sectionMove.Code);

            // 6. AI trying to insert ad-hoc perga-block html -> PolicyDenied!
            var htmlEx = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.CommitReportAsync(tplTrackId, tplLeadId,
                    new ReportCommitRequest(report.Anchor, [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Html, null, TestJson.FromObject(new { path = "x.html" }))])));
            Assert.Equal(KernelErrorCode.PolicyDenied, htmlEx.Code);

            // 7. AI editing normal prose section -> Allowed!
            var proseOk = await ctx.Kernel.CommitReportAsync(tplTrackId, tplLeadId,
                new ReportCommitRequest(report.Anchor, [new BlockOp.ReplaceSectionOp("Target", "Updated prose target.")]));
            Assert.Equal(report.DocRev + 1, proseOk.DocRev);
        }
        finally
        {
            if (Directory.Exists(tempPkgDir)) Directory.Delete(tempPkgDir, true);
            if (Directory.Exists(badPkgDir)) Directory.Delete(badPkgDir, true);
        }
    }

    [Fact]
    public async Task FlowTemplateInputsInterpolation_AndRoutingEnforcement_WorkWithoutMutatingFlowFile()
    {
        var realVm = new Perga.PolicyVm.PolicyVm();
        await using var ctx = new KernelTestContext(policyVm: realVm);
        ctx.Kernel.Templates.RegisterTemplate("""
            export default defineTemplate({
              id: "routed-tpl",
              title: "Routed Template",
              inputs: {
                targetEnv: { type: "string", default: "staging" }
              },
              tasks: {
                routing: [
                  { when: { role: "review" }, provider: "codex", model: "gpt-5.4", enforce: true }
                ]
              }
            });
            """);

        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "routed-tpl");
        var trackId = snap.Track.Id;
        var flowPath = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;

        var observedCmds = new List<string>();
        ctx.CommandRunner.Handler = (req, _) =>
        {
            observedCmds.Add(req.Command);
            return Task.FromResult(new ShellCommandResult(0, "ok", "", false, 5));
        };

        // 1. Disallowed model on role=review with enforce: true -> rejected!
        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                {
                  "id": "bad-route",
                  "title": "Bad Route",
                  "do": { "kind": "run", "role": "review", "provider": "claude", "model": "claude-haiku", "prompt": "Review" }
                }
              ]
            }
            """);
        var badCommit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human);
        Assert.False(badCommit.Ok);
        Assert.Contains(badCommit.Diagnostics, e => e.Path == "$.nodes[0].do" && e.Message.Contains("codex", StringComparison.OrdinalIgnoreCase));

        // 2. Cron node with ${inputs.targetEnv} placeholder stays unexpanded in flow.json, but interpolates in projection & at fire time!
        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                {
                  "id": "env-check",
                  "title": "Check ${inputs.targetEnv}",
                  "when": { "cron": "0 9 * * *", "tz": "UTC" },
                  "do": { "kind": "run", "cmd": "deploy-check --env=${inputs.targetEnv}" }
                }
              ]
            }
            """);
        var okCommit = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human);
        Assert.True(okCommit.Ok);
        Assert.Contains("${inputs.targetEnv}", await File.ReadAllTextAsync(flowPath), StringComparison.Ordinal);
        Assert.Equal("Check staging", Assert.Single(okCommit.Flow.Nodes).Title);

        // Update template inputs via MCP -> flow.json keeps ${inputs.targetEnv}, projection & execution interpolate "prod"!
        var leadReg = ctx.Kernel.McpSessionBridge.RegisterSession(trackId, snap.Track.LeadSessionId!, isLead: true, null, null, []);
        await ctx.Kernel.InvokeKernelMcpToolAsync(leadReg.Principal, KernelMcpToolNames.TemplateUpdateInputs,
            TestJson.FromObject(new { inputs = new { targetEnv = "prod" } }));
        Assert.Contains("${inputs.targetEnv}", await File.ReadAllTextAsync(flowPath), StringComparison.Ordinal);
        var updatedFlow = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Equal("Check prod", Assert.Single(updatedFlow.Projection.Nodes).Title);

        ctx.Time.Advance(TimeSpan.FromMinutes(2));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.Contains("deploy-check --env=prod", observedCmds);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> HoldTurnAsync(
        string sessionId,
        TaskCompletionSource<string> started,
        Task release,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.TextDelta("working");
        started.TrySetResult(sessionId);
        await release.WaitAsync(ct).ConfigureAwait(false);
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> DefaultCompletedTurnAsync()
    {
        yield return new UnifiedAgentEvent.TextDelta("done");
        yield return new UnifiedAgentEvent.TurnCompleted(null);
        await Task.CompletedTask;
    }
}
