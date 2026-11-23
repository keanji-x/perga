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

public sealed partial class TrackFlowTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    [Fact]
    public void EditingABorrowedNodeForFreshRunsClearsThePreviousSessionBinding()
    {
        var borrowed = new FlowNodeSpecDto("worker", new("run", Session: "ses_borrowed", Prompt: "Review"));
        var previous = TrackFlowReconciler.BuildOrUpdateTaskForNode("track", borrowed, null, 1) with
        {
            Status = TaskRunStatus.Done, SessionId = "ses_borrowed", RunCount = 1, FinishedAtMs = 2
        };
        var fresh = borrowed with { Do = borrowed.Do with { Session = null } };
        var edited = TrackFlowReconciler.BuildOrUpdateTaskForNode("track", fresh, previous, 3, previousNode: borrowed);
        Assert.Null(edited.SessionId);
        Assert.False(FlowConstants.IsFlowSessionReuse(edited));
    }

    [Fact]
    public async Task FailureReservedBeforeCancellationCannotSettleOrReleaseWorkspace()
    {
        var bridge = new ObservedMcpSessionBridge();
        await using var ctx = new KernelTestContext(mcpSessionBridge: bridge);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var workerStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shuttingDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (session, _, ct) =>
            HoldTurnAsync(session.NativeThreadId!, workerStarted, release.Task, ct);
        ctx.AgentRuntime.OnOpenedSession = agent => agent.DisposeHandler = async () =>
        {
            shuttingDown.TrySetResult();
            await release.Task;
        };
        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId,
            FlowFixture.Worker("worker", "Worker", "read_write"));
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new("attention", new("run", Cmd: "attention"), When: new(After: [new("worker", "attention")])),
            new FlowNodeSpecDto("next", new("run", Cmd: "next", Access: "read_write")));
        Task<TaskDto>? cancellation = null;
        // Revocation runs in the failure reservation, before its settlement can be queued.
        bridge.OnRevoke = id =>
        {
            if (id == worker.Session.Id)
                cancellation ??= ctx.Kernel.CancelTaskAsync(trackId, null, worker.Task.Id);
        };
        try
        {
            var failure = ctx.Kernel.CompleteTaskFailAsync(trackId, worker.Session.Id, "Late failure");
            await shuttingDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<KernelException>(() => failure.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelErrorCode.Conflict, error.Code);
            var tasks = await ctx.Kernel.ListTasksAsync(trackId);
            Assert.Equal(TaskRunStatus.Canceled, Assert.Single(tasks, task => task.Id == worker.Task.Id).Status);
            Assert.False(Assert.Single(tasks, task => task.TaskKey == "attention").Ready);
            Assert.Equal(TaskRunStatus.Pending, Assert.Single(tasks, task => task.TaskKey == "next").Status);
            Assert.Empty(ctx.CommandRunner.ExecutedCommands);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(TaskRunStatus.Canceled, (await cancellation!.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("next", Assert.Single(ctx.CommandRunner.ExecutedCommands).Command);
    }

    private sealed class ObservedMcpSessionBridge : IMcpSessionBridge
    {
        private readonly InMemoryMcpSessionBridge _inner = new();
        public Action<string>? OnRevoke { get; set; }

        public McpSessionRegistration RegisterSession(string trackId, string sessionId, bool isLead,
            string? boundTaskId, int? taskRunCount, IReadOnlyList<string> enabledPlugins, string? runtimeBindingId = null, string? baseUriOverride = null) =>
            _inner.RegisterSession(trackId, sessionId, isLead, boundTaskId, taskRunCount, enabledPlugins, runtimeBindingId, baseUriOverride);
        public McpSessionPrincipal? ResolveToken(string token) => _inner.ResolveToken(token);
        public string? GetTokenForSession(string sessionId) => _inner.GetTokenForSession(sessionId);
        public void UpdateLeadStatus(string trackId, string? newLeadSessionId) => _inner.UpdateLeadStatus(trackId, newLeadSessionId);
        public void UpdateTrackPlugins(string trackId, IReadOnlyList<string> enabledPlugins) => _inner.UpdateTrackPlugins(trackId, enabledPlugins);
        public void RevokeSession(string sessionId, string? runtimeBindingId = null)
        {
            _inner.RevokeSession(sessionId, runtimeBindingId);
            OnRevoke?.Invoke(sessionId);
        }
        public void RevokeTrack(string trackId) => _inner.RevokeTrack(trackId);
    }

    [Fact]
    public async Task CancelDisablesRecurringClockAfterTrackCloseCanceledOnlyTheRun()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.CommandRunner.Handler = async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            return new ShellCommandResult(0, "", "", false, 1);
        };
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("pulse", new("run", Cmd: "pulse"), When: new(Cron: "* * * * *", Tz: "UTC")));
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closed = await ctx.Kernel.CloseTrackAsync(trackId, snapshot.Track.LeadSessionId!);
        Assert.NotNull(closed.ClosedAtMs);
        Assert.Equal(TaskRunStatus.Canceled, Assert.Single(await ctx.Kernel.ListTasksAsync(trackId)).Status);
        Assert.Null((await ctx.Kernel.PatchTrackAsync(trackId, new(Closed: false))).ClosedAtMs);
        var before = await ctx.Kernel.GetTrackFlowAsync(trackId);
        var canceled = await ctx.Kernel.CancelTaskAsync(trackId, null, "pulse");
        Assert.Equal(TaskRunStatus.Canceled, canceled.Status);
        var after = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Equal(before.FlowRev + 1, after.FlowRev);
        Assert.True(Assert.Single(after.Spec.GetProperty("nodes").EnumerateArray()).GetProperty("canceled").GetBoolean());
        Assert.Equal(TrackScheduleStates.Canceled, (await ctx.Store.GetAsync(trackId, "flow:pulse", default))!.State);
        await ctx.Kernel.CancelTaskAsync(trackId, null, "pulse");
        Assert.Equal(after.FlowRev, (await ctx.Kernel.GetTrackFlowAsync(trackId)).FlowRev);
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("later", new("run", Cmd: "later"), When: new(At: "2099-01-01T00:00:00Z")));
        ctx.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds()));
        Assert.Single(ctx.CommandRunner.ExecutedCommands);
    }

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

        public bool HasTimerDueAt(DateTimeOffset dueAt)
        {
            lock (_lock) return _timers.Any(timer => !timer.Disposed && timer.DueTime == dueAt);
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

        // Explicit `canceled: true` in flow.json succeeds!
        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 1,
              "nodes": [
                { "id": "active-job", "title": "Running", "canceled": true, "do": { "kind": "run", "cmd": "echo hold" } },
                { "id": "nightly", "when": { "cron": "0 9 * * *", "tz": "UTC" }, "do": { "kind": "run", "cmd": "echo hi" } }
              ]
            }
            """);
        var canceled = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 1, Human);
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
        Assert.False(openedR1.Disposed);
        releaseRound1Turn.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.True(openedR1.Disposed);
        Assert.Equal("completed", Assert.Single(ctx.Store.GetTurnsForSession(reviewerSessionId)).Status);

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
        var followUp = ctx.AgentRuntime.OpenedSessions.Last(s => s.LaunchSpec.StateKey == reviewerSessionId);
        Assert.NotSame(openedR1, followUp);
        Assert.Equal(originalThreadId, followUp.LaunchSpec.ResumeNativeThreadId);

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
        Assert.NotSame(followUp, lastOpened);
        Assert.Equal(originalThreadId, lastOpened.LaunchSpec.ResumeNativeThreadId);
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
        var lead = await ctx.OpenMcpCallerAsync(snap.Track.LeadSessionId!);
        await ctx.Kernel.InvokeKernelMcpToolAsync(lead, KernelMcpToolNames.TemplateUpdateInputs,
            TestJson.FromObject(new { inputs = new { targetEnv = "prod" } }));
        Assert.Contains("${inputs.targetEnv}", await File.ReadAllTextAsync(flowPath), StringComparison.Ordinal);
        var updatedFlow = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Equal("Check prod", Assert.Single(updatedFlow.Projection.Nodes).Title);

        ctx.Time.Advance(TimeSpan.FromMinutes(2));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.Contains("deploy-check --env=prod", observedCmds);
    }

    [Fact]
    public async Task FlowPreview_UsesRoutedAccessForGateAndReportsExplicitDenialOnce()
    {
        await using var ctx = new KernelTestContext(policyVm: new Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate("""
            export default {
              id: 'routed-review', title: 'Routed Review',
              tasks: {
                requireGateForReadWrite: true,
                routing: [{ when: { role: 'review' }, access: 'read_only', enforce: true }]
              }
            };
            """);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync(templateId: "routed-review");
        var trackId = snapshot.Track.Id;
        var flowPath = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;
        const string flow = """
            {
              "rev": 0,
              "nodes": [{ "id": "review", "title": "Review", "do": { "kind": "run", "role": "review", "prompt": "Review changes" } }]
            }
            """;

        await File.WriteAllTextAsync(flowPath, flow);
        Assert.True((await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human)).Ok);

        await File.WriteAllTextAsync(flowPath,
            flow.Replace("\"role\": \"review\",", "\"role\": \"review\", \"access\": \"read_write\",", StringComparison.Ordinal));
        var denied = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: true, expectedFlowRev: 0, Human);
        Assert.False(denied.Ok);
        Assert.Contains("Task routing requires access read_only", Assert.Single(denied.Diagnostics).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GateRequiredTemplateAllowsCommandWatchesButRequiresGatesForWritableRuns(bool dryRun)
    {
        await using var ctx = new KernelTestContext(policyVm: new Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate("""
            export default defineTemplate({
              id: "gate-required",
              title: "Gate Required",
              tasks: { requireGateForReadWrite: true }
            });
            """);
        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "gate-required");
        var trackId = snap.Track.Id;

        const string commandWatch = """
            {"rev":0,"nodes":[{"id":"shell","do":{"kind":"watch","cmd":"probe"}}]}
            """;
        var rejected = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun, 0, Human,
            content: commandWatch.Replace("\"kind\":\"watch\"", "\"kind\":\"run\"", StringComparison.Ordinal));
        Assert.False(rejected.Ok);
        var diagnostic = Assert.Single(rejected.Diagnostics);
        Assert.Equal("$.nodes[0].do.gate", diagnostic.Path);
        Assert.Contains("gate", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, (await ctx.Kernel.GetTrackFlowAsync(trackId)).FlowRev);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(trackId)).Tasks);
        Assert.Empty(ctx.CommandRunner.ExecutedCommands);

        var committed = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun, 0, Human, content: commandWatch);
        Assert.True(committed.Ok, JsonSerializer.Serialize(committed.Diagnostics));
        if (dryRun)
        {
            Assert.Empty(ctx.CommandRunner.ExecutedCommands);
            return;
        }
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("probe", Assert.Single(ctx.CommandRunner.ExecutedCommands).Command);
        Assert.Equal("ok", Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Archived).Outcome);
    }

    [Fact]
    public async Task BackgroundGc_ArchivesUnreachableOneShotBranch_AndAdvancesFlowRev()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var flowPath = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;

        ctx.CommandRunner.Handler = (_, _) => Task.FromResult(new ShellCommandResult(0, "ok", "", false, 2));

        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                { "id": "build", "title": "Build", "do": { "kind": "run", "cmd": "echo build" } },
                { "id": "deploy", "title": "Deploy", "when": { "after": ["build"], "on": "ok" }, "do": { "kind": "run", "cmd": "echo deploy" } },
                { "id": "fix-build", "title": "Fix Build", "when": { "after": ["build"], "on": "attention" }, "do": { "kind": "run", "cmd": "echo fix" } }
              ]
            }
            """);
        var committed = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human);
        Assert.True(committed.Ok);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var afterGc = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Empty(afterGc.Projection.Nodes);
        Assert.True(afterGc.FlowRev > 1);
        Assert.Equal(3, afterGc.Projection.Archived.Count);
        var skipped = Assert.Single(afterGc.Projection.Archived, a => a.Id == "fix-build");
        Assert.Equal("canceled", skipped.Status);
    }

    [Fact]
    public async Task BusyCronTick_DoesNotExhaustMaxOccurrences()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var flowPath = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        ctx.CommandRunner.Handler = async (_, ct) =>
        {
            var n = Interlocked.Increment(ref executions);
            if (n == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(ct);
            }
            return new ShellCommandResult(0, $"run-{n}", "", false, 2);
        };

        await File.WriteAllTextAsync(flowPath, """
            {
              "rev": 0,
              "nodes": [
                {
                  "id": "limited-cron",
                  "title": "Limited Cron",
                  "when": { "cron": "*/5 * * * *", "tz": "UTC", "maxOccurrences": 2 },
                  "do": { "kind": "run", "cmd": "echo tick" }
                }
              ]
            }
            """);
        Assert.True((await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun: false, expectedFlowRev: 0, Human)).Ok);

        // First fire starts run #1 and holds it busy.
        ctx.Time.Advance(TimeSpan.FromMinutes(6));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second fire while run #1 is busy must NOT consume occurrence #2!
        ctx.Time.Advance(TimeSpan.FromMinutes(6));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        var midFlow = await ctx.Kernel.GetTrackFlowAsync(trackId);
        var midNode = Assert.Single(midFlow.Projection.Nodes);
        Assert.Equal(1, midNode.FiredCount);
        Assert.NotNull(midNode.NextFireAtMs);

        // Finish run #1, then fire again -> run #2 actually executes!
        releaseFirst.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        ctx.Time.Advance(TimeSpan.FromMinutes(6));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        Assert.Equal(2, executions);
        var finalNode = Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes);
        Assert.Equal(2, finalNode.RunCount);
        Assert.Equal(2, finalNode.FiredCount);
        Assert.Null(finalNode.NextFireAtMs);
    }


    [Fact]
    public async Task GcRetainsAllPrerequisitesOfAlternativeOutcomeJoin()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var path = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.CommandRunner.Handler = async (req, ct) =>
        {
            if (req.Command == "hold")
                await hold.Task.WaitAsync(ct);
            return new ShellCommandResult(0, "", "", false, 1);
        };

        await File.WriteAllTextAsync(path, """
            {"rev":0,"nodes":[
              {"id":"a","do":{"kind":"run","cmd":"a"}},
              {"id":"b","do":{"kind":"run","cmd":"b"}},
              {"id":"sibling","when":{"after":["a"]},"do":{"kind":"run","cmd":"hold"}},
              {"id":"join","when":{"after":[{"node":"a","on":"attention"},"b"]},"do":{"kind":"run","cmd":"join"}}
            ]}
            """);
        try
        {
            Assert.True((await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human)).Ok);
            await WaitUntilAsync(() => Task.FromResult(ctx.CommandRunner.ExecutedCommands.Any(cmd => cmd.Command == "hold")));

            var flow = await ctx.Kernel.GetTrackFlowAsync(trackId);
            Assert.Equal(new[] { "a", "b", "join", "sibling" }, flow.Projection.Nodes.Select(node => node.Id).Order());
            Assert.Empty(flow.Projection.Archived);
            var preview = await ctx.Kernel.CommitTrackFlowAsync(trackId, true, flow.FlowRev, Human);
            Assert.True(preview.Ok, JsonSerializer.Serialize(preview.Diagnostics));
        }
        finally
        {
            hold.TrySetResult();
        }
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
    }

    [Fact]
    public async Task PendingConsumerRefreshesWaveKeyWithReverseDeclarationOrder()
    {
        var forge = new FakeGitHubForgeService();
        await using var ctx = new KernelTestContext(gitHubForgeService: forge);
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var path = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.CommandRunner.Handler = async (req, ct) =>
        {
            if (req.Command == "hold")
                await release.Task.WaitAsync(ct);
            return new ShellCommandResult(0, "", "", false, 1);
        };
        await File.WriteAllTextAsync(path, """
            {"rev":0,"nodes":[
              {"id":"finish","when":{"after":["prepare"]},"do":{"kind":"run","cmd":"finish"}},
              {"id":"prepare","when":{"after":["cron"]},"do":{"kind":"watch","prChecks":2}},
              {"id":"cron","when":{"cron":"* * * * *","tz":"UTC"},"do":{"kind":"watch","prChecks":1}},
              {"id":"hold","do":{"kind":"run","cmd":"hold"}}
            ]}
            """);
        try
        {
            Assert.True((await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human)).Ok);
            await WaitUntilAsync(() => Task.FromResult(ctx.CommandRunner.ExecutedCommands.Any(cmd => cmd.Command == "hold")));
            for (var wave = 1; wave <= 2; wave++)
            {
                ctx.Time.Advance(TimeSpan.FromMinutes(2));
                await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
                await WaitUntilAsync(async () =>
                {
                    var flow = await ctx.Kernel.GetTrackFlowAsync(trackId);
                    return flow.Projection.Nodes.Any(node => node.Id == "prepare" && node.RunCount == wave && node.Status == "done");
                });
            }
            Assert.DoesNotContain(ctx.CommandRunner.ExecutedCommands, cmd => cmd.Command == "finish");
        }
        finally
        {
            release.TrySetResult();
        }
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.Single(ctx.CommandRunner.ExecutedCommands, cmd => cmd.Command == "finish");
        var final = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Contains(final.Projection.Nodes, node => node.Id == "finish" && node.Status == "done" && node.RunCount == 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(75)]
    public async Task QueuedWatchResultCannotChangeCanceledTask(int exitCode)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var path = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;
        var probe = new TaskCompletionSource<ShellCommandResult>();
        ctx.CommandRunner.Handler = (_, _) => probe.Task;
        const string original = """
            {"rev":0,"nodes":[
              {"id":"watch","do":{"kind":"watch","cmd":"probe"}},
              {"id":"consumer","when":{"after":["watch"]},"do":{"kind":"run","cmd":"consumer"}}
            ]}
            """;
        await File.WriteAllTextAsync(path, original);
        Assert.True((await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human)).Ok);
        await WaitUntilAsync(() => Task.FromResult(ctx.CommandRunner.ExecutedCommands.Any(cmd => cmd.Command == "probe")));
        await File.WriteAllTextAsync(path, original.Replace("\"rev\":0", "\"rev\":1", StringComparison.Ordinal)
            .Replace("\"id\":\"watch\",", "\"id\":\"watch\",\"canceled\":true,", StringComparison.Ordinal));

        var releaseMailbox = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mailboxHold = ctx.Kernel.Actors.GetOrActivate(trackId).EnqueueAsync(_ => releaseMailbox.Task);
        var cancel = ctx.Kernel.CommitTrackFlowAsync(trackId, false, 1, Human);
        try
        {
            // Resume the probe inline while cancellation is in front of its result in the mailbox.
            probe.SetResult(new ShellCommandResult(exitCode, "returned before cancellation", "", false, 1));
        }
        finally
        {
            releaseMailbox.TrySetResult();
        }
        await mailboxHold;
        Assert.True((await cancel.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
        var flow = await ctx.Kernel.GetTrackFlowAsync(trackId);
        var archived = Assert.Single(flow.Projection.Archived, node => node.Id == "watch");
        Assert.Equal("canceled", archived.Status);
        var track = await ctx.Kernel.GetTrackAggregateAsync(trackId);
        var task = Assert.Single(track.Tasks, task => task.TaskKey == "watch");
        Assert.DoesNotContain("returned before cancellation", task.Meta?.GetRawText() ?? "");
        Assert.DoesNotContain(ctx.CommandRunner.ExecutedCommands, req => req.Command == "consumer");
    }

    [Fact]
    public async Task CancelingAndChangingAWatchKeepsItsStartedWorkspaceLeaseUntilExit()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var path = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.CommandRunner.Handler = async (req, ct) =>
        {
            if (req.Command == "probe")
            {
                using var registration = ct.Register(() => stopping.TrySetResult());
                await exit.Task;
            }
            return new ShellCommandResult(0, "", "", false, 1);
        };
        await File.WriteAllTextAsync(path, """
            {"rev":0,"nodes":[
              {"id":"watch","do":{"kind":"watch","cmd":"probe"}}
            ]}
            """);
        Assert.True((await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human)).Ok);
        await WaitUntilAsync(() => Task.FromResult(ctx.CommandRunner.ExecutedCommands.Any(cmd => cmd.Command == "probe")));
        await File.WriteAllTextAsync(path, """
            {"rev":1,"nodes":[
              {"id":"watch","canceled":true,"do":{"kind":"watch","prChecks":1}},
              {"id":"next","do":{"kind":"run","cmd":"next"}}
            ]}
            """);
        var cancel = ctx.Kernel.CommitTrackFlowAsync(trackId, false, 1, Human);
        try
        {
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var track = await ctx.Kernel.GetTrackAggregateAsync(trackId);
            Assert.Equal(TaskRunStatus.Pending, Assert.Single(track.Tasks, task => task.TaskKey == "next").Status);
        }
        finally
        {
            exit.TrySetResult();
        }
        Assert.True((await cancel.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.Single(ctx.CommandRunner.ExecutedCommands, req => req.Command == "next");
    }

    [Fact]
    public async Task WatchAccessMatchesProbeWorkspaceUsage()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var path = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FilePath!;
        await File.WriteAllTextAsync(path, """
            {"rev":0,"nodes":[
              {"id":"shell","when":{"cron":"* * * * *"},"do":{"kind":"watch","cmd":"probe"}},
              {"id":"forge","when":{"cron":"* * * * *"},"do":{"kind":"watch","prChecks":1}}
            ]}
            """);
        Assert.True((await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human)).Ok);
        var track = await ctx.Kernel.GetTrackAggregateAsync(trackId);
        Assert.Equal(AccessMode.ReadWrite, Assert.Single(track.Tasks, task => task.TaskKey == "shell").Access);
        Assert.Equal(AccessMode.ReadOnly, Assert.Single(track.Tasks, task => task.TaskKey == "forge").Access);
    }

    [Theory]
    [InlineData("1s", false)]
    [InlineData("5s", true)]
    public void WatchIntervalFloorMatchesPublishedSchema(string interval, bool accepted)
    {
        using var schema = ReadPublishedFlowSchema();
        var watchVariants = schema.RootElement.GetProperty("$defs").GetProperty("do").GetProperty("oneOf").EnumerateArray()
            .Select(variant => variant.GetProperty("properties"))
            .Where(properties => properties.GetProperty("kind").GetProperty("const").GetString() == "watch").ToArray();
        Assert.Equal(2, watchVariants.Length);
        foreach (var properties in watchVariants)
        {
            var pattern = properties.GetProperty("interval").GetProperty("pattern").GetString()!;
            Assert.Equal(accepted, System.Text.RegularExpressions.Regex.IsMatch(interval, pattern));
            var probe = properties.TryGetProperty("prChecks", out _) ? "\"prChecks\":1" : "\"cmd\":\"probe\"";
            var (spec, _) = TrackFlowParser.Parse($$"""
                {"rev":0,"nodes":[{"id":"watch","do":{"kind":"watch",{{probe}},"interval":"{{interval}}" } }]}
                """);
            Assert.Equal(accepted, spec is not null);
        }
    }

    [Theory]
    [InlineData(2147483647L, true)]
    [InlineData(2147483648L, false)]
    public void WatchPrNumberBoundsMatchPublishedSchema(long pr, bool accepted)
    {
        using var schema = ReadPublishedFlowSchema();
        var constraint = schema.RootElement.GetProperty("$defs").GetProperty("do").GetProperty("oneOf").EnumerateArray()
            .Select(variant => variant.GetProperty("properties"))
            .Single(properties => properties.TryGetProperty("prChecks", out _)).GetProperty("prChecks");
        Assert.Equal(accepted, pr >= constraint.GetProperty("minimum").GetInt64() && pr <= constraint.GetProperty("maximum").GetInt64());
        var (spec, _) = TrackFlowParser.Parse($$"""
            {"rev":0,"nodes":[{"id":"watch","do":{"kind":"watch","prChecks":{{pr}} } }]}
            """);
        Assert.Equal(accepted, spec is not null);
    }

    private static JsonDocument ReadPublishedFlowSchema()
    {
        using var stream = typeof(TrackFlowFileStore).Assembly.GetManifestResourceStream("Perga.FlowSchemaV1")!;
        return JsonDocument.Parse(stream);
    }

    [Fact]
    public void TrackFlowFileStore_RejectsPathTraversalAndSymlinks()
    {
        var tempDir = Directory.CreateTempSubdirectory("perga-flow-sec-");
        try
        {
            var store = new Perga.Kernel.Flow.TrackFlowFileStore(tempDir.FullName);
            var badEx = Assert.Throws<KernelException>(() => store.GetFlowFilePath("../escape"));
            Assert.Equal(KernelErrorCode.InvalidArgument, badEx.Code);

            var outside = Path.Combine(tempDir.FullName, "secret.json");
            File.WriteAllText(outside, """{"rev":0,"nodes":[]}""");
            var trackDir = Path.Combine(tempDir.FullName, "tracks", "trk_sym");
            Directory.CreateDirectory(trackDir);
            File.CreateSymbolicLink(Path.Combine(trackDir, "flow.json"), outside);

            var symEx = Assert.Throws<KernelException>(() => store.ReadFlowFile("trk_sym"));
            Assert.Equal(KernelErrorCode.PolicyDenied, symEx.Code);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
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
