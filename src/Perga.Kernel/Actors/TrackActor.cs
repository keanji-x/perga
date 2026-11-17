using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Kernel.Actors;

public sealed class TrackActor : IAsyncDisposable
{
    private readonly ITrackStore _store;
    private readonly IReportDocEngine _reportEngine;
    private readonly IPolicyVm _policyVm;
    private readonly ITemplateCatalog _templateCatalog;
    private readonly IAgentRuntime _agentRuntime;
    private readonly IWorkspaceDriverRegistry _workspaceDrivers;
    private readonly ITerminalBridgeClient _terminalClient;
    private readonly IMcpSessionBridge _mcpBridge;
    private readonly IShellCommandRunner _commandRunner;
    private readonly EventHub _eventHub;
    private readonly KernelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string, string, CancellationToken, Task>? _crossTrackWakeCallback;

    private readonly Channel<Func<TrackActor, Task>> _mailbox;
    private readonly CancellationTokenSource _actorShutdownCts = new();
    private readonly Task _mailboxLoopTask;

    // Actor-owned mutable state (mutated strictly on the single-writer mailbox loop)
    private bool _hydrated;
    private TrackDto _track = null!;
    private ReportDocument _report = null!;
    private CompiledPolicy? _policy;
    private string? _templateSnapshotSource;
    private MountedWorkspace? _mountedWorkspace;
    private readonly List<TaskDto> _tasks = [];
    private readonly Dictionary<string, AgentSessionDto> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AskDto> _asks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalDto> _terminals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextTurnIndexBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IAgentSession> _openAgentSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveTurnState> _activeTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<(string TriggerKind, string Prompt)>> _pendingWakes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingAskWaiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveAttemptState> _activeAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SessionId, string IdempotencyKey), Task<SessionTurnDto>> _idempotencyTurns = new();
    private readonly ConcurrentDictionary<Guid, Task> _backgroundJobs = new();

    private int _inFlightExternalOps;
    private int _queuedMailboxOps;
    private long _lastActivityAtMs;
    private bool _disposed;

    internal TrackActor(
        string trackId,
        ITrackStore store,
        IReportDocEngine reportEngine,
        IPolicyVm policyVm,
        ITemplateCatalog templateCatalog,
        IAgentRuntime agentRuntime,
        IWorkspaceDriverRegistry workspaceDrivers,
        ITerminalBridgeClient terminalClient,
        IMcpSessionBridge mcpBridge,
        IShellCommandRunner commandRunner,
        EventHub eventHub,
        KernelOptions options,
        TimeProvider timeProvider,
        Func<string, string, string, CancellationToken, Task>? crossTrackWakeCallback = null)
    {
        TrackId = trackId;
        _store = store;
        _reportEngine = reportEngine;
        _policyVm = policyVm;
        _templateCatalog = templateCatalog;
        _agentRuntime = agentRuntime;
        _workspaceDrivers = workspaceDrivers;
        _terminalClient = terminalClient;
        _mcpBridge = mcpBridge;
        _commandRunner = commandRunner;
        _eventHub = eventHub;
        _options = options;
        _timeProvider = timeProvider;
        _crossTrackWakeCallback = crossTrackWakeCallback;
        _lastActivityAtMs = NowMs();

        _mailbox = Channel.CreateUnbounded<Func<TrackActor, Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _mailboxLoopTask = Task.Run(ProcessMailboxLoopAsync);
    }

    public string TrackId { get; }

    public long LastActivityAtMs => Volatile.Read(ref _lastActivityAtMs);

    public bool HasActiveWork =>
        Volatile.Read(ref _queuedMailboxOps) > 0 ||
        Volatile.Read(ref _inFlightExternalOps) > 0 ||
        !_backgroundJobs.IsEmpty;

    internal void SeedHydratedState(
        TrackDto track,
        ReportDocument report,
        IReadOnlyList<TaskDto> tasks,
        IReadOnlyList<AgentSessionDto> sessions,
        CompiledPolicy? policy,
        string? templateSnapshotSource,
        MountedWorkspace mountedWorkspace)
    {
        _track = track;
        _report = report;
        _policy = policy;
        _templateSnapshotSource = templateSnapshotSource;
        _mountedWorkspace = mountedWorkspace;

        _tasks.Clear();
        _tasks.AddRange(tasks);

        _sessions.Clear();
        foreach (var s in sessions)
        {
            var isLead = string.Equals(s.Id, track.LeadSessionId, StringComparison.Ordinal);
            var normalized = s.IsLead == isLead ? s : s with { IsLead = isLead };
            _sessions[normalized.Id] = normalized;
            _nextTurnIndexBySession[normalized.Id] = 0;
        }

        _hydrated = true;
        TouchActivity();
    }

    public async Task<T> EnqueueAsync<T>(Func<TrackActor, Task<T>> work, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TouchActivity();
        Interlocked.Increment(ref _queuedMailboxOps);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = _mailbox.Writer.TryWrite(async actor =>
        {
            try
            {
                TouchActivity();
                var result = await work(actor).ConfigureAwait(false);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                TouchActivity();
                Interlocked.Decrement(ref _queuedMailboxOps);
            }
        });

        if (!written)
        {
            Interlocked.Decrement(ref _queuedMailboxOps);
            throw new ObjectDisposedException(nameof(TrackActor), $"TrackActor '{TrackId}' is shutting down.");
        }

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public Task EnqueueAsync(Func<TrackActor, Task> work, CancellationToken ct = default)
    {
        return EnqueueAsync<bool>(async actor =>
        {
            await work(actor).ConfigureAwait(false);
            return true;
        }, ct);
    }

    // ------------------- Public Actor Operations -------------------

    public Task<TrackAggregateSnapshot> GetAggregateSnapshotAsync(CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            return BuildSnapshotCore();
        }, ct);
    }

    public Task<MountedWorkspace> EnsureMountedAsync(CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            return await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<TrackStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var unreadMails = await _store.ListMailsAsync(TrackId, unreadOnly: true, ct).ConfigureAwait(false);
            return new TrackStatusDto(
                Track: _track,
                Sessions: OrderedSessionsCore(),
                Tasks: _tasks.ToArray(),
                OpenAsks: _asks.Values.Where(a => a.Status == AskStatus.Open).OrderBy(a => a.CreatedAtMs).ToArray(),
                Terminals: _terminals.Values.OrderBy(t => t.CreatedAtMs).ToArray(),
                UnreadMailCount: unreadMails.Count);
        }, ct);
    }

    public Task<TrackDto> PatchTrackAsync(PatchTrackRequest req, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var now = NowMs();
            var nextTrack = _track;
            var changed = false;

            if (req.Title is not null && !string.Equals(req.Title, nextTrack.Title, StringComparison.Ordinal))
            {
                nextTrack = nextTrack with { Title = req.Title };
                changed = true;
            }

            if (req.Closed.HasValue)
            {
                long? nextClosed = req.Closed.Value ? (nextTrack.ClosedAtMs ?? now) : null;
                if (nextClosed != nextTrack.ClosedAtMs)
                {
                    nextTrack = nextTrack with { ClosedAtMs = nextClosed };
                    changed = true;
                }
            }

            if (req.Pinned.HasValue)
            {
                long? nextPinned = req.Pinned.Value ? (nextTrack.PinnedAtMs ?? now) : null;
                if (nextPinned != nextTrack.PinnedAtMs)
                {
                    nextTrack = nextTrack with { PinnedAtMs = nextPinned };
                    changed = true;
                }
            }

            if (req.SortOrder.HasValue && Math.Abs(req.SortOrder.Value - nextTrack.SortOrder) > double.Epsilon)
            {
                nextTrack = nextTrack with { SortOrder = req.SortOrder.Value };
                changed = true;
            }

            if (req.EnabledPlugins is not null)
            {
                nextTrack = nextTrack with { EnabledPlugins = req.EnabledPlugins.ToArray() };
                _mcpBridge.UpdateTrackPlugins(TrackId, nextTrack.EnabledPlugins);
                changed = true;
            }

            if (req.LeadSessionId is not null && !string.Equals(req.LeadSessionId, nextTrack.LeadSessionId, StringComparison.Ordinal))
            {
                await SwitchLeadSessionCoreAsync(req.LeadSessionId, now, ct).ConfigureAwait(false);
                nextTrack = _track with
                {
                    Title = nextTrack.Title,
                    ClosedAtMs = nextTrack.ClosedAtMs,
                    PinnedAtMs = nextTrack.PinnedAtMs,
                    SortOrder = nextTrack.SortOrder,
                    EnabledPlugins = nextTrack.EnabledPlugins
                };
                changed = true;
            }

            if (changed)
            {
                nextTrack = nextTrack with { UpdatedAtMs = now };
                _track = nextTrack;
                await EmitTrackUpdatedCoreAsync(now, ct).ConfigureAwait(false);
            }

            return _track;
        }, ct);
    }

    public Task<TrackDto> RenameTrackBySessionAsync(string callerSessionId, string newTitle, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, "perga_track_rename");

            var now = NowMs();
            _track = _track with { Title = newTitle, UpdatedAtMs = now };
            await EmitTrackUpdatedCoreAsync(now, ct).ConfigureAwait(false);
            return _track;
        }, ct);
    }

    public async Task<TrackDto> CloseTrackBySessionAsync(string callerSessionId, CancellationToken ct = default)
    {
        var closedTrack = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, "perga_track_close");
            await CancelAllInFlightAttemptsCoreAsync("Track closed.", ct).ConfigureAwait(false);

            var now = NowMs();
            _track = _track with { ClosedAtMs = _track.ClosedAtMs ?? now, UpdatedAtMs = now };
            await EmitTrackUpdatedCoreAsync(now, ct).ConfigureAwait(false);
            return _track;
        }, ct).ConfigureAwait(false);

        return closedTrack;
    }

    public async Task TeardownAndDeleteTrackAsync(CancellationToken ct = default)
    {
        (WorkspaceSpec Spec, IWorkspaceDriver Driver, string? CloseCmd, string Cwd) teardownInfo = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);

            // 1. Cancel active turns
            foreach (var turn in _activeTurns.Values.ToArray())
            {
                turn.TurnCts.Cancel();
                try
                {
                    await turn.AgentSession.CancelTurnAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort turn cancellation.
                }
            }

            // 2. Cancel pending ask waiters
            foreach (var kvp in _pendingAskWaiters.ToArray())
            {
                _pendingAskWaiters.Remove(kvp.Key);
                kvp.Value.TrySetCanceled(ct);
            }

            // 3. Cancel active attempts & discard leases
            await CancelAllInFlightAttemptsCoreAsync("Track deleted.", ct).ConfigureAwait(false);

            // 4. Kill open terminals
            foreach (var term in _terminals.Values.Where(t => t.Status == TerminalStatus.Running).ToArray())
            {
                try
                {
                    await _terminalClient.KillAsync(term.Id, graceMs: 1000, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort terminal cleanup.
                }
            }

            // 5. Dispose open agent sessions & revoke MCP tokens
            foreach (var kvp in _openAgentSessions.ToArray())
            {
                _openAgentSessions.Remove(kvp.Key);
                await kvp.Value.DisposeAsync().ConfigureAwait(false);
            }

            _mcpBridge.RevokeTrack(TrackId);

            var driver = _workspaceDrivers.GetRequired(_track.WorkspaceDriver);
            var spec = ExtractWorkspaceSpec(_track);
            var closeCmd = _policy?.Manifest.WorkspaceCloseCmd;
            var cwd = _mountedWorkspace?.MergedCwd ?? _track.Cwd;
            return (spec, driver, closeCmd, cwd);
        }, ct).ConfigureAwait(false);

        // Run WorkspaceCloseCmd before tearing down workspace
        if (!string.IsNullOrWhiteSpace(teardownInfo.CloseCmd))
        {
            try
            {
                await _commandRunner.RunAsync(
                    new ShellCommandRequest(
                        teardownInfo.CloseCmd,
                        teardownInfo.Cwd,
                        TimeSpan.FromSeconds(_options.DefaultHookTimeoutSecs)),
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close command before physical teardown.
            }
        }

        await teardownInfo.Driver.TeardownTrackAsync(teardownInfo.Spec, ct).ConfigureAwait(false);
        await _store.DeleteTrackAsync(TrackId, ct).ConfigureAwait(false);
    }

    // ------------------- Report Read & Commit -------------------

    public Task<ReportReadResult> ReadReportAsync(string? callerSessionId = null, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (callerSessionId is not null && !_sessions.ContainsKey(callerSessionId))
            {
                throw KernelException.NotFound($"Session '{callerSessionId}' not found on track '{TrackId}'.");
            }

            var rendered = _reportEngine.RenderMarkdown(_report, withMarkers: true);
            var knownRevs = _report.Blocks.ToDictionary(b => b.Id, b => b.Rev, StringComparer.Ordinal);
            var anchor = new ReadAnchor(_report.DocRev, knownRevs, WholeDocumentRead: true);

            return new ReportReadResult(
                DocRev: _report.DocRev,
                Summary: _report.Summary,
                RenderedMarkdown: rendered,
                Blocks: _report.Blocks,
                Tasks: _tasks.ToArray(),
                Anchor: anchor,
                Diagnostics: []);
        }, ct);
    }

    public async Task<ReportReadResult> UpdateReportBlockByHumanAsync(
        string blockId,
        HumanBlockUpdateRequest req,
        CancellationToken ct = default)
    {
        var commitResult = await CommitReportInternalAsync(
            EditActorKind.Human,
            callerSessionId: null,
            buildOps: currentDoc =>
            {
                var existing = currentDoc.Blocks.FirstOrDefault(b => string.Equals(b.Id, blockId, StringComparison.Ordinal));
                var kind = req.Kind ?? existing?.Kind ?? ReportBlockKind.Prose;
                var markdown = req.Markdown ?? existing?.Markdown;
                var payload = req.Payload ?? existing?.Payload;

                var knownRevs = new Dictionary<string, int>(StringComparer.Ordinal);
                if (existing is not null)
                {
                    knownRevs[blockId] = req.KnownBlockRev;
                }

                var anchor = new ReadAnchor(req.DocRev, knownRevs, WholeDocumentRead: existing is null && req.DocRev == currentDoc.DocRev);
                var ops = new BlockOp[]
                {
                    new BlockOp.UpsertBlockOp(blockId, kind, markdown, payload)
                };

                return new ReportCommitRequest(
                    Anchor: anchor,
                    Ops: ops,
                    Summary: req.Summary,
                    CommitMessage: req.CommitMessage ?? $"Human updated block {blockId}");
            },
            ct).ConfigureAwait(false);

        await WakeLeadAsync(
            triggerKind: "wake",
            wakePrompt: $"Human updated report block '{blockId}' (docRev={commitResult.DocRev}). Review the latest report changes.",
            ct: ct).ConfigureAwait(false);

        return commitResult;
    }

    public Task<ReportReadResult> CommitReportBySessionAsync(
        string callerSessionId,
        ReportCommitRequest req,
        CancellationToken ct = default)
    {
        return CommitReportInternalAsync(
            actorKind: null,
            callerSessionId: callerSessionId,
            buildOps: _ => req,
            ct: ct);
    }

    private async Task<ReportReadResult> CommitReportInternalAsync(
        EditActorKind? actorKind,
        string? callerSessionId,
        Func<ReportDocument, ReportCommitRequest> buildOps,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var phase1 = await EnqueueAsync<(ReportReadResult? Completed, PendingAskSuspension? Ask, ReportCommitRequest EffectiveRequest)>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);

                var (editorRole, boundAttemptId) = ResolveEditorIdentityCore(actorKind, callerSessionId);
                var effectiveReq = buildOps(_report);

                TaskBlockSynchronizer.ValidatePreCommitBoundAttemptFence(boundAttemptId, _report, effectiveReq.Ops);
                var preview = _reportEngine.ApplyCommit(_report, effectiveReq.Anchor, effectiveReq.Ops, effectiveReq.Summary);
                TaskBlockSynchronizer.ValidatePostCommitBoundAttemptFence(boundAttemptId, _report, preview);

                var touchedSections = CollectTouchedSections(_report, preview, effectiveReq.Ops);
                var nextProseChars = preview.NextDocument.Blocks
                    .Where(b => b.Kind == ReportBlockKind.Prose)
                    .Sum(b => b.Markdown?.Length ?? 0);

                ValidateManifestReportRulesCore(editorRole, touchedSections, nextProseChars);

                if (_policy is not null)
                {
                    var prevMd = _reportEngine.RenderMarkdown(_report, withMarkers: false);
                    var nextMd = _reportEngine.RenderMarkdown(preview.NextDocument, withMarkers: false);
                    var ctx = new PolicyReportCommitContext(
                        TrackId: TrackId,
                        EditorRole: editorRole,
                        PrevMarkdown: prevMd,
                        NextMarkdown: nextMd,
                        TouchedSections: touchedSections,
                        NextProseChars: nextProseChars,
                        Inputs: _track.TemplateInputs);

                    var eval = _policyVm.GuardReportCommit(_policy, ctx, _track.TemplateState);
                    await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);

                    switch (eval.Output)
                    {
                        case GuardDecision.Deny deny:
                            throw KernelException.PolicyDenied(deny.Reason);

                        case GuardDecision.AskHuman askHuman:
                            var suspension = await CreateAskSuspensionCoreAsync(
                                askHuman.Title,
                                askHuman.Options,
                                callerSessionId,
                                ct).ConfigureAwait(false);
                            return (null, suspension, effectiveReq);
                    }
                }

                var result = await FinalizeReportCommitCoreAsync(
                    preview,
                    callerSessionId,
                    effectiveReq.CommitMessage ?? $"Report commit ({editorRole})",
                    ct).ConfigureAwait(false);
                return (result, null, effectiveReq);
            }, ct).ConfigureAwait(false);

            if (phase1.Completed is not null)
            {
                return phase1.Completed;
            }

            var askSuspension = phase1.Ask!;
            if (askSuspension.ShouldWakeLead)
            {
                await DispatchLeadWakePromptIfNeededAsync("wake", askSuspension.LeadWakePrompt!, ct).ConfigureAwait(false);
            }

            var humanAnswer = await askSuspension.WaiterTask.WaitAsync(ct).ConfigureAwait(false);
            if (!AskAnswerEvaluator.IsApproved(humanAnswer, askSuspension.Options))
            {
                throw KernelException.PolicyDenied($"Report commit denied by human on ask '{askSuspension.Ask.Id}'.");
            }

            return await EnqueueAsync(async _ =>
            {
                var (editorRole, boundAttemptId) = ResolveEditorIdentityCore(actorKind, callerSessionId);
                TaskBlockSynchronizer.ValidatePreCommitBoundAttemptFence(boundAttemptId, _report, phase1.EffectiveRequest.Ops);
                var verifiedCommit = _reportEngine.ApplyCommit(
                    _report,
                    phase1.EffectiveRequest.Anchor,
                    phase1.EffectiveRequest.Ops,
                    phase1.EffectiveRequest.Summary);
                TaskBlockSynchronizer.ValidatePostCommitBoundAttemptFence(boundAttemptId, _report, verifiedCommit);
                ValidateManifestReportRulesCore(
                    editorRole,
                    CollectTouchedSections(_report, verifiedCommit, phase1.EffectiveRequest.Ops),
                    verifiedCommit.NextDocument.Blocks.Where(b => b.Kind == ReportBlockKind.Prose).Sum(b => b.Markdown?.Length ?? 0));

                return await FinalizeReportCommitCoreAsync(
                    verifiedCommit,
                    callerSessionId,
                    phase1.EffectiveRequest.CommitMessage ?? $"Report commit ({editorRole}, human-approved)",
                    ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    // ------------------- Sessions, Streaming Turns & Epoch Compaction -------------------

    public Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            return OrderedSessionsCore();
        }, ct);
    }

    public Task<AgentSessionDto> CreateSubagentSessionAsync(CreateSessionRequest req, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);

            var now = NowMs();
            var session = new AgentSessionDto(
                Id: $"ses_{Guid.NewGuid():N}",
                TrackId: TrackId,
                IsLead: false,
                BoundTaskId: null,
                BoundAttemptId: null,
                Title: string.IsNullOrWhiteSpace(req.Title) ? "Subagent" : req.Title,
                Provider: string.IsNullOrWhiteSpace(req.Provider) ? GetDefaultProviderCore() : req.Provider!,
                Model: req.Model ?? GetDefaultModelCore(),
                NativeThreadId: null,
                Epoch: 1,
                Status: "idle",
                ContextUsageRatio: 0.0,
                ArchivedAtMs: null,
                CreatedAtMs: now,
                UpdatedAtMs: now);

            _sessions[session.Id] = session;
            _nextTurnIndexBySession[session.Id] = 0;
            await PersistAndEmitSessionUpdatedCoreAsync(session, now, ct).ConfigureAwait(false);
            return session;
        }, ct);
    }

    public Task<SessionItemsPageDto> GetSessionItemsAsync(string sessionId, long afterId = 0, int limit = 100, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            var items = await _store.ListSessionItemsAsync(sessionId, afterId, limit, ct).ConfigureAwait(false);
            var activeTurn = _activeTurns.TryGetValue(sessionId, out var running) ? running.TurnDto : null;
            return new SessionItemsPageDto(items, activeTurn, session);
        }, ct);
    }

    public async Task<SessionTurnDto> SendMessageAsync(string sessionId, SendMessageRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            throw KernelException.InvalidArgument("IdempotencyKey is required.");
        }

        var dispatch = await EnqueueAsync<(Task<SessionTurnDto>? ExistingTask, ActiveTurnState? SteerTarget, TaskCompletionSource<SessionTurnDto>? OwnedTurnTcs)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireSessionCore(sessionId);

            var dedupKey = (sessionId, req.IdempotencyKey);
            if (_idempotencyTurns.TryGetValue(dedupKey, out var existing))
            {
                return (existing, null, null);
            }

            if (_activeTurns.TryGetValue(sessionId, out var active))
            {
                if (!req.SteerIfRunning)
                {
                    throw KernelException.Conflict($"Session '{sessionId}' already has an active turn in progress.");
                }

                return (null, active, null);
            }

            var turnTcs = new TaskCompletionSource<SessionTurnDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            _idempotencyTurns[dedupKey] = turnTcs.Task;
            return (null, null, turnTcs);
        }, ct).ConfigureAwait(false);

        if (dispatch.ExistingTask is not null)
        {
            return await dispatch.ExistingTask.WaitAsync(ct).ConfigureAwait(false);
        }

        if (dispatch.SteerTarget is not null)
        {
            var steered = await dispatch.SteerTarget.AgentSession.TrySteerAsync(req.Content, ct).ConfigureAwait(false);
            if (!steered)
            {
                await EnqueueAsync(_ =>
                {
                    EnqueuePendingWakeCore(sessionId, "user", req.Content);
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }

            await EnqueueAsync(_ =>
            {
                _idempotencyTurns[(sessionId, req.IdempotencyKey)] = Task.FromResult(dispatch.SteerTarget.TurnDto);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);

            return dispatch.SteerTarget.TurnDto;
        }

        var ownedTcs = dispatch.OwnedTurnTcs!;
        try
        {
            var completed = await ExecuteSessionTurnPipelineAsync(sessionId, "user", req.Content, ct).ConfigureAwait(false);
            ownedTcs.TrySetResult(completed);
            return completed;
        }
        catch (Exception ex)
        {
            ownedTcs.TrySetException(ex);
            await EnqueueAsync(_ =>
            {
                _idempotencyTurns.Remove((sessionId, req.IdempotencyKey));
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task InterruptSessionAsync(string sessionId, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireSessionCore(sessionId);

            if (_activeTurns.TryGetValue(sessionId, out var active))
            {
                active.Interrupted = true;
                active.TurnCts.Cancel();
                await active.AgentSession.CancelTurnAsync(ct).ConfigureAwait(false);
            }
        }, ct);
    }

    public async Task<AgentSessionDto> CompactSessionAsync(string sessionId, CancellationToken ct = default)
    {
        return await CompactSessionInternalAsync(sessionId, ct).ConfigureAwait(false);
    }

    // ------------------- Tasks, Governance & Attempt Settlement -------------------

    public Task<IReadOnlyList<TaskDto>> ListTasksAsync(CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            return (IReadOnlyList<TaskDto>)_tasks.ToArray();
        }, ct);
    }

    public Task<IReadOnlyList<TaskAttemptDto>> ListTaskAttemptsAsync(string taskKeyOrId, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var task = RequireTaskCore(taskKeyOrId);
            return await _store.ListAttemptsForTaskAsync(task.Id, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<TaskDto> CancelTaskAsync(string callerSessionId, string taskKeyOrId, string? reason = null, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, "perga_task_cancel");
            var task = RequireTaskCore(taskKeyOrId);

            var now = NowMs();
            TaskAttemptDto? settledAttempt = null;
            JsonElement? nextTemplateState = null;

            if (task.LatestAttemptId is not null &&
                _activeAttempts.TryGetValue(task.LatestAttemptId, out var activeAttempt))
            {
                settledAttempt = await CancelAndDiscardActiveAttemptCoreAsync(activeAttempt, reason ?? "Canceled by governance session.", now, ct)
                    .ConfigureAwait(false);
            }

            if (_policy is not null)
            {
                var settledCtx = new PolicyTaskSettledContext(
                    TrackId: TrackId,
                    TaskKey: task.TaskKey,
                    AccessMode: FormatAccessModeForPolicy(task.Access),
                    AttemptStatus: "canceled",
                    WorkerResult: settledAttempt?.WorkerResult,
                    AttemptMeta: settledAttempt?.Meta ?? JsonHelpers.EmptyObject,
                    GateVerdict: null,
                    Inputs: _track.TemplateInputs);
                var eval = _policyVm.OnTaskSettled(_policy, settledCtx, _track.TemplateState);
                _track = _track with { TemplateState = eval.NextState };
                nextTemplateState = eval.NextState;
            }

            var updatedTask = task with
            {
                Status = TaskRunStatus.Canceled,
                Ready = false,
                StatusDetail = reason ?? "Canceled.",
                UpdatedAtMs = now
            };
            ReplaceTaskInMemoryCore(updatedTask);

            await PersistAndEmitTaskUpdatedCoreAsync(updatedTask, settledAttempt, nextTemplateState, now, ct).ConfigureAwait(false);
            await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
            return updatedTask;
        }, ct);
    }

    public Task<TaskDto> AcceptTaskAsync(string callerSessionId, string taskKeyOrId, string? note = null, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, "perga_task_accept");
            var task = RequireTaskCore(taskKeyOrId);

            var now = NowMs();
            var updated = task with
            {
                SemanticVerdict = SemanticVerdict.Accepted,
                SemanticNote = note,
                UpdatedAtMs = now
            };
            ReplaceTaskInMemoryCore(updated);
            await PersistAndEmitTaskUpdatedCoreAsync(updated, attempt: null, nextTemplateState: null, now, ct).ConfigureAwait(false);
            return updated;
        }, ct);
    }

    public Task<TaskDto> RejectTaskAsync(
        string callerSessionId,
        string taskKeyOrId,
        string note,
        bool reQueue = false,
        CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, "perga_task_reject");
            var task = RequireTaskCore(taskKeyOrId);

            var now = NowMs();
            var nextStatus = reQueue && task.Ready ? TaskRunStatus.Pending : TaskRunStatus.Failed;
            var updated = task with
            {
                SemanticVerdict = reQueue ? SemanticVerdict.None : SemanticVerdict.Rejected,
                SemanticNote = note,
                Status = nextStatus,
                StatusDetail = $"Rejected: {note}",
                UpdatedAtMs = now
            };
            ReplaceTaskInMemoryCore(updated);
            await PersistAndEmitTaskUpdatedCoreAsync(updated, attempt: null, nextTemplateState: null, now, ct).ConfigureAwait(false);

            if (reQueue)
            {
                await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
            }

            return updated;
        }, ct);
    }

    public async Task<TaskDto> CompleteAttemptDoneAsync(
        string callerSessionId,
        JsonElement? workerResult = null,
        CancellationToken ct = default)
    {
        var attemptId = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var caller = RequireBoundAttemptSessionCore(callerSessionId, "perga_task_done");
            return caller.BoundAttemptId!;
        }, ct).ConfigureAwait(false);

        return await CompleteAttemptDoneByAttemptIdAsync(attemptId, workerResult, ct).ConfigureAwait(false);
    }

    public async Task<TaskDto> CompleteAttemptFailAsync(
        string callerSessionId,
        string reason,
        JsonElement? workerResult = null,
        CancellationToken ct = default)
    {
        var attemptId = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var caller = RequireBoundAttemptSessionCore(callerSessionId, "perga_task_fail");
            return caller.BoundAttemptId!;
        }, ct).ConfigureAwait(false);

        return await CompleteAttemptFailByAttemptIdAsync(attemptId, reason, workerResult, ct).ConfigureAwait(false);
    }

    // ------------------- Asks, Mail, Terminals, GuardToolCall & Schedules -------------------

    public async Task<JsonElement> AskUserAsync(
        string? callerSessionId,
        IReadOnlyList<AskQuestionDto> questions,
        CancellationToken ct = default)
    {
        if (questions.Count == 0)
        {
            throw KernelException.InvalidArgument("At least one question is required for perga_user_ask.");
        }

        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var suspension = await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                if (callerSessionId is not null)
                {
                    RequireSessionCore(callerSessionId);
                }

                return await CreateAskSuspensionFromQuestionsCoreAsync(questions, callerSessionId, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (suspension.ShouldWakeLead)
            {
                await DispatchLeadWakePromptIfNeededAsync("wake", suspension.LeadWakePrompt!, ct).ConfigureAwait(false);
            }

            return await suspension.WaiterTask.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    public async Task<AskDto> AnswerAskAsync(string askId, JsonElement answer, CancellationToken ct = default)
    {
        var outcome = await EnqueueAsync<(AskDto AnsweredAsk, TaskCompletionSource<JsonElement>? Waiter, bool ShouldWakeLead)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (!_asks.TryGetValue(askId, out var existingAsk))
            {
                throw KernelException.NotFound($"Ask '{askId}' not found on track '{TrackId}'.");
            }

            if (existingAsk.Status != AskStatus.Open)
            {
                throw KernelException.Conflict($"Ask '{askId}' is already '{existingAsk.Status}'.");
            }

            var now = NowMs();
            var clonedAnswer = answer.Clone();
            var answered = existingAsk with
            {
                Status = AskStatus.Answered,
                Answer = clonedAnswer,
                AnsweredAtMs = now
            };
            _asks[askId] = answered;

            var evt = new EventEnvelopeDto(
                EventId: null,
                AreaId: _track.AreaId,
                TrackId: TrackId,
                Kind: KernelEventKinds.AskChanged,
                Payload: JsonHelpers.ToElement(answered),
                CreatedAtMs: now);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.UpsertAskAsync(answered, evt, ct).ConfigureAwait(false);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);

            _pendingAskWaiters.Remove(askId, out var waiter);

            // Determine whether Lead is ALREADY actively streaming the exact turn that owns this ask.
            var leadOwnsActiveAsk = false;
            if (_track.LeadSessionId is { } leadId &&
                _activeTurns.TryGetValue(leadId, out var leadTurn) &&
                leadTurn.OwnedAskIds.Contains(askId))
            {
                leadOwnsActiveAsk = true;
            }

            var shouldWakeLead = !leadOwnsActiveAsk && _track.ClosedAtMs is null && !string.IsNullOrEmpty(_track.LeadSessionId);
            return (answered, waiter, shouldWakeLead);
        }, ct).ConfigureAwait(false);

        outcome.Waiter?.TrySetResult(outcome.AnsweredAsk.Answer!.Value);

        if (outcome.ShouldWakeLead)
        {
            var answerText = outcome.AnsweredAsk.Answer!.Value.GetRawText();
            await WakeLeadAsync(
                triggerKind: "wake",
                wakePrompt: $"User answered ask '{askId}': {answerText}",
                ct: ct).ConfigureAwait(false);
        }

        return outcome.AnsweredAsk;
    }

    public async Task<MailDto> SendCrossTrackMailAsync(
        string callerSessionId,
        string toTrackId,
        string summary,
        string body,
        Func<MailDto, CancellationToken, Task> deliverToTargetTrack,
        CancellationToken ct = default)
    {
        var mail = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, "perga_mail_send");

            var now = NowMs();
            return new MailDto(
                Id: $"mail_{Guid.NewGuid():N}",
                FromTrackId: TrackId,
                ToTrackId: toTrackId,
                Summary: summary,
                Body: body,
                ReadAtMs: null,
                CreatedAtMs: now);
        }, ct).ConfigureAwait(false);

        await deliverToTargetTrack(mail, ct).ConfigureAwait(false);
        return mail;
    }

    internal async Task ReceiveMailAsync(MailDto mail, CancellationToken ct = default)
    {
        await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var evt = new EventEnvelopeDto(
                EventId: null,
                AreaId: _track.AreaId,
                TrackId: TrackId,
                Kind: KernelEventKinds.MailReceived,
                Payload: JsonHelpers.ToElement(mail),
                CreatedAtMs: mail.CreatedAtMs);

            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.UpsertMailAsync(mail, evt, ct).ConfigureAwait(false);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await WakeLeadAsync(
            triggerKind: "wake",
            wakePrompt: $"New cross-track mail '{mail.Id}' from track '{mail.FromTrackId}': {mail.Summary}\n\n{mail.Body}",
            ct: ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MailDto>> ListMailsAsync(bool unreadOnly = false, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            return await _store.ListMailsAsync(TrackId, unreadOnly, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task<MailDto> ReadMailAsync(string mailId, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var mails = await _store.ListMailsAsync(TrackId, unreadOnly: false, ct).ConfigureAwait(false);
            var target = mails.FirstOrDefault(m => string.Equals(m.Id, mailId, StringComparison.Ordinal))
                         ?? throw KernelException.NotFound($"Mail '{mailId}' not found on track '{TrackId}'.");

            if (target.ReadAtMs is not null)
            {
                return target;
            }

            var now = NowMs();
            var readMail = target with { ReadAtMs = now };
            var evt = new EventEnvelopeDto(
                EventId: null,
                AreaId: _track.AreaId,
                TrackId: TrackId,
                Kind: KernelEventKinds.MailReceived,
                Payload: JsonHelpers.ToElement(readMail),
                CreatedAtMs: now);

            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.UpsertMailAsync(readMail, evt, ct).ConfigureAwait(false);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
            return readMail;
        }, ct);
    }

    public Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            return (IReadOnlyList<TerminalDto>)_terminals.Values.OrderBy(t => t.CreatedAtMs).ToArray();
        }, ct);
    }

    public Task<TerminalDto> OpenTerminalAsync(string? callerSessionId, OpenTerminalRequest req, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var mounted = await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);

            string? boundAttemptId = null;
            string effectiveCwd = mounted.MergedCwd;

            if (callerSessionId is not null)
            {
                var caller = RequireSessionCore(callerSessionId);
                boundAttemptId = caller.BoundAttemptId;
                if (boundAttemptId is not null &&
                    _activeAttempts.TryGetValue(boundAttemptId, out var activeAttempt))
                {
                    effectiveCwd = activeAttempt.Lease.EffectiveCwd;
                }
            }

            if (!string.IsNullOrWhiteSpace(req.Cwd))
            {
                effectiveCwd = Path.IsPathRooted(req.Cwd)
                    ? req.Cwd
                    : Path.GetFullPath(Path.Combine(effectiveCwd, req.Cwd));
            }

            if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                var existing = _terminals.Values.FirstOrDefault(t =>
                    string.Equals(t.IdempotencyKey, req.IdempotencyKey, StringComparison.Ordinal) &&
                    t.Status == TerminalStatus.Running);
                if (existing is not null)
                {
                    return existing;
                }
            }

            var terminalId = $"trm_{Guid.NewGuid():N}";
            var program = string.IsNullOrWhiteSpace(req.Program) ? "/bin/bash" : req.Program!;
            var ensureReq = new TermEnsureRequest(
                TerminalId: terminalId,
                Program: program,
                Args: req.Args ?? [],
                Cwd: effectiveCwd,
                Env: req.Env ?? new Dictionary<string, string>(),
                Cols: req.Cols,
                Rows: req.Rows);

            await _terminalClient.EnsureAsync(ensureReq, ct).ConfigureAwait(false);

            var now = NowMs();
            var terminal = new TerminalDto(
                Id: terminalId,
                TrackId: TrackId,
                AttemptId: boundAttemptId,
                IdempotencyKey: req.IdempotencyKey,
                Title: string.IsNullOrWhiteSpace(req.Title) ? "Terminal" : req.Title!,
                Program: program,
                Cwd: effectiveCwd,
                Status: TerminalStatus.Running,
                ExitCode: null,
                CreatedAtMs: now,
                ExitedAtMs: null);

            _terminals[terminal.Id] = terminal;
            await PersistAndEmitTerminalChangedCoreAsync(terminal, now, ct).ConfigureAwait(false);
            return terminal;
        }, ct);
    }

    public async Task<TermObserveResult> ObserveTerminalAsync(TermObserveRequest req, CancellationToken ct = default)
    {
        await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
            if (!_terminals.ContainsKey(req.TerminalId))
            {
                throw KernelException.NotFound($"Terminal '{req.TerminalId}' not found on track '{TrackId}'.");
            }
        }, ct).ConfigureAwait(false);

        var observed = await _terminalClient.ObserveAsync(req, ct).ConfigureAwait(false);
        if (observed.Exited)
        {
            await EnqueueAsync(async _ =>
            {
                if (_terminals.TryGetValue(req.TerminalId, out var term) && term.Status != TerminalStatus.Exited)
                {
                    var now = NowMs();
                    var updated = term with
                    {
                        Status = TerminalStatus.Exited,
                        ExitCode = observed.ExitCode,
                        ExitedAtMs = now
                    };
                    _terminals[updated.Id] = updated;
                    await PersistAndEmitTerminalChangedCoreAsync(updated, now, ct).ConfigureAwait(false);
                }
            }, ct).ConfigureAwait(false);
        }

        return observed;
    }

    public async Task<TermActionResult> SendTerminalInputAsync(TermActionRequest req, CancellationToken ct = default)
    {
        await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
            if (!_terminals.ContainsKey(req.TerminalId))
            {
                throw KernelException.NotFound($"Terminal '{req.TerminalId}' not found on track '{TrackId}'.");
            }
        }, ct).ConfigureAwait(false);

        return await _terminalClient.SendActionAsync(req, ct).ConfigureAwait(false);
    }

    public async Task ResizeTerminalAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct = default)
    {
        await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (!_terminals.ContainsKey(terminalId))
            {
                throw KernelException.NotFound($"Terminal '{terminalId}' not found on track '{TrackId}'.");
            }
        }, ct).ConfigureAwait(false);

        await _terminalClient.ResizeAsync(terminalId, cols, rows, ct).ConfigureAwait(false);
    }

    public async Task<TerminalDto> CloseTerminalAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default)
    {
        await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (!_terminals.ContainsKey(terminalId))
            {
                throw KernelException.NotFound($"Terminal '{terminalId}' not found on track '{TrackId}'.");
            }
        }, ct).ConfigureAwait(false);

        await _terminalClient.KillAsync(terminalId, graceMs, ct).ConfigureAwait(false);

        return await EnqueueAsync(async _ =>
        {
            var existing = _terminals[terminalId];
            var now = NowMs();
            var closed = existing with
            {
                Status = TerminalStatus.Exited,
                ExitCode = existing.ExitCode ?? 0,
                ExitedAtMs = existing.ExitedAtMs ?? now
            };
            _terminals[terminalId] = closed;
            await PersistAndEmitTerminalChangedCoreAsync(closed, now, ct).ConfigureAwait(false);
            return closed;
        }, ct).ConfigureAwait(false);
    }

    public async Task GuardMcpToolCallAsync(
        string sessionId,
        string mcpServer,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var askSuspension = await EnqueueAsync<PendingAskSuspension?>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var caller = RequireSessionCore(sessionId);

                if (!string.Equals(mcpServer, "perga", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mcpServer, "vcs", StringComparison.OrdinalIgnoreCase) &&
                    !_track.EnabledPlugins.Contains(mcpServer, StringComparer.Ordinal))
                {
                    throw KernelException.Forbidden(
                        $"MCP plugin '{mcpServer}' is not enabled on track '{TrackId}'.");
                }

                if (_policy is null)
                {
                    return null;
                }

                var ctx = new PolicyToolCallContext(
                    TrackId: TrackId,
                    SessionId: caller.Id,
                    IsLead: caller.IsLead,
                    BoundAttemptId: caller.BoundAttemptId,
                    McpServer: mcpServer,
                    ToolName: toolName,
                    Arguments: arguments.ValueKind == JsonValueKind.Undefined ? JsonHelpers.EmptyObject : arguments.Clone(),
                    Inputs: _track.TemplateInputs);

                var eval = _policyVm.GuardToolCall(_policy, ctx, _track.TemplateState);
                await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);

                switch (eval.Output)
                {
                    case GuardDecision.Allow:
                        return null;

                    case GuardDecision.Deny deny:
                        throw KernelException.PolicyDenied(deny.Reason);

                    case GuardDecision.AskHuman askHuman:
                        return await CreateAskSuspensionCoreAsync(
                            askHuman.Title,
                            askHuman.Options,
                            caller.Id,
                            ct).ConfigureAwait(false);

                    default:
                        return null;
                }
            }, ct).ConfigureAwait(false);

            if (askSuspension is null)
            {
                return;
            }

            if (askSuspension.ShouldWakeLead)
            {
                await DispatchLeadWakePromptIfNeededAsync("wake", askSuspension.LeadWakePrompt!, ct).ConfigureAwait(false);
            }

            var answer = await askSuspension.WaiterTask.WaitAsync(ct).ConfigureAwait(false);
            if (!AskAnswerEvaluator.IsApproved(answer, askSuspension.Options))
            {
                throw KernelException.PolicyDenied(
                    $"MCP tool '{mcpServer}:{toolName}' denied by human on ask '{askSuspension.Ask.Id}'.");
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    public async Task TriggerScheduleWakeAsync(string scheduleId, CancellationToken ct = default)
    {
        var prompt = await EnqueueAsync<string?>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_track.ClosedAtMs is not null || string.IsNullOrEmpty(_track.LeadSessionId))
            {
                return null;
            }

            if (_policy is null)
            {
                return $"Scheduled wake '{scheduleId}' triggered.";
            }

            var eval = _policyVm.BuildScheduleWakePrompt(_policy, scheduleId, _track.TemplateInputs, _track.TemplateState);
            await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(eval.Output))
            {
                return eval.Output;
            }

            var spec = _policy.Manifest.Schedules.FirstOrDefault(s => string.Equals(s.Id, scheduleId, StringComparison.Ordinal));
            return spec?.StaticWakePrompt ?? $"Scheduled wake '{scheduleId}' triggered.";
        }, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await WakeLeadAsync("wake", prompt!, ct).ConfigureAwait(false);
        }
    }

    public Task WakeLeadAsync(string triggerKind, string wakePrompt, CancellationToken ct = default)
    {
        return DispatchLeadWakePromptIfNeededAsync(triggerKind, wakePrompt, ct);
    }

    public async Task WaitForBackgroundWorkAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var jobs = _backgroundJobs.Values.ToArray();
            if (jobs.Length == 0)
            {
                break;
            }

            await Task.WhenAll(jobs).WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // ------------------- Internal Core Implementations -------------------

    private async Task ProcessMailboxLoopAsync()
    {
        try
        {
            while (await _mailbox.Reader.WaitToReadAsync(_actorShutdownCts.Token).ConfigureAwait(false))
            {
                while (_mailbox.Reader.TryRead(out var item))
                {
                    await item(this).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal actor shutdown.
        }
    }

    private async Task EnsureHydratedCoreAsync(CancellationToken ct)
    {
        if (_hydrated)
        {
            return;
        }

        var snapshot = await _store.LoadTrackAggregateAsync(TrackId, ct).ConfigureAwait(false)
                       ?? throw KernelException.NotFound($"Track '{TrackId}' not found.");

        _track = snapshot.Track;
        _report = snapshot.Report;
        _templateSnapshotSource = snapshot.TemplateSnapshotSource;

        if (!string.IsNullOrWhiteSpace(snapshot.TemplateSnapshotSource))
        {
            _policy = _templateCatalog.GetOrCompileSnapshot(snapshot.TemplateSnapshotSource!);
        }
        else if (!string.IsNullOrWhiteSpace(_track.TemplateId))
        {
            _policy = _templateCatalog.TryGetPolicy(_track.TemplateId!);
        }

        _tasks.Clear();
        _tasks.AddRange(snapshot.Tasks);

        _sessions.Clear();
        foreach (var s in snapshot.Sessions)
        {
            var isLead = string.Equals(s.Id, _track.LeadSessionId, StringComparison.Ordinal);
            var normalized = s.IsLead == isLead ? s : s with { IsLead = isLead };
            _sessions[normalized.Id] = normalized;

            var existingItems = await _store.ListSessionItemsAsync(normalized.Id, afterId: 0, limit: 1000, ct)
                .ConfigureAwait(false);
            _nextTurnIndexBySession[normalized.Id] = ComputeMaxTurnIndex(existingItems);
        }

        _asks.Clear();
        foreach (var ask in snapshot.OpenAsks)
        {
            _asks[ask.Id] = ask;
        }

        var terminals = await _store.ListTerminalsAsync(TrackId, ct).ConfigureAwait(false);
        _terminals.Clear();
        foreach (var term in terminals)
        {
            _terminals[term.Id] = term;
        }

        // Hydration Mount Hook: ALWAYS ensure workspace is mounted before starting any session, terminal, or attempt.
        await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
        _hydrated = true;
    }

    private async Task<MountedWorkspace> EnsureWorkspaceMountedCoreAsync(CancellationToken ct)
    {
        if (_mountedWorkspace is not null)
        {
            return _mountedWorkspace;
        }

        var driver = _workspaceDrivers.GetRequired(_track.WorkspaceDriver);
        var spec = ExtractWorkspaceSpec(_track);
        _mountedWorkspace = await driver.EnsureMountedAsync(spec, ct).ConfigureAwait(false);
        return _mountedWorkspace;
    }

    internal static WorkspaceSpec ExtractWorkspaceSpec(TrackDto track)
    {
        if (track.WorkspaceSpec.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var parsed = JsonHelpers.Deserialize<WorkspaceSpec>(track.WorkspaceSpec);
                if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.MergedCwd))
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Fall back to synthesized WorkspaceSpec below.
            }
        }

        return new WorkspaceSpec(
            DriverId: track.WorkspaceDriver,
            LogicalRoot: track.Cwd,
            LowerDirs: [],
            UpperDir: null,
            WorkDir: null,
            MergedCwd: track.Cwd,
            Metadata: track.WorkspaceSpec.ValueKind == JsonValueKind.Undefined
                ? JsonHelpers.EmptyObject
                : track.WorkspaceSpec.Clone());
    }

    private static IReadOnlyList<OverlayMountSpec>? MapOverlayLayers(IReadOnlyList<WorkspaceOverlayLayer>? layers)
    {
        if (layers is null || layers.Count == 0)
        {
            return null;
        }

        return layers
            .Select(l => new OverlayMountSpec(l.LowerDirs, l.UpperDir, l.WorkDir, l.TargetPath))
            .ToArray();
    }

    private async Task<ReportReadResult> FinalizeReportCommitCoreAsync(
        ReportCommitResult commitResult,
        string? authorSessionId,
        string commitMessage,
        CancellationToken ct)
    {
        var now = NowMs();
        var requireGate = _policy?.Manifest.RequireGateForReadWrite ?? false;
        var syncOutcome = TaskBlockSynchronizer.SyncTasksFromReport(
            TrackId,
            commitResult.NextDocument,
            _tasks,
            requireGate,
            now);

        foreach (var supersededTaskId in syncOutcome.SupersededOrRemovedActiveTaskIds)
        {
            var active = _activeAttempts.Values.FirstOrDefault(a =>
                string.Equals(a.TaskId, supersededTaskId, StringComparison.Ordinal));
            if (active is not null)
            {
                await CancelAndDiscardActiveAttemptCoreAsync(active, "Task contract superseded or removed in report.", now, ct)
                    .ConfigureAwait(false);
            }
        }

        var compressedBlocks = _reportEngine.CompressZstd(commitResult.NextDocument.Blocks);
        var reportPayload = JsonHelpers.ToElement(new
        {
            docRev = commitResult.NextDocument.DocRev,
            summary = commitResult.NextDocument.Summary,
            changes = commitResult.Changes
        });
        var reportEvt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.ReportUpdated,
            Payload: reportPayload,
            CreatedAtMs: now);

        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.SaveReportAndTasksAsync(
            TrackId,
            commitResult.NextDocument,
            compressedBlocks,
            authorSessionId,
            commitMessage,
            syncOutcome.SyncedTasks,
            _track.TemplateState,
            reportEvt,
            ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, reportEvt, ct).ConfigureAwait(false);

        _report = commitResult.NextDocument;
        _tasks.Clear();
        _tasks.AddRange(syncOutcome.SyncedTasks);

        var taskEvt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.TaskUpdated,
            Payload: JsonHelpers.ToElement(new { tasks = _tasks }),
            CreatedAtMs: now);
        await _store.AppendEventAsync(taskEvt, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, taskEvt, ct).ConfigureAwait(false);

        await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);

        var rendered = _reportEngine.RenderMarkdown(_report, withMarkers: true);
        var knownRevs = _report.Blocks.ToDictionary(b => b.Id, b => b.Rev, StringComparer.Ordinal);
        return new ReportReadResult(
            DocRev: _report.DocRev,
            Summary: _report.Summary,
            RenderedMarkdown: rendered,
            Blocks: _report.Blocks,
            Tasks: _tasks.ToArray(),
            Anchor: new ReadAnchor(_report.DocRev, knownRevs, WholeDocumentRead: true),
            Diagnostics: syncOutcome.Diagnostics);
    }

    internal Task TriggerSchedulerAsync(CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
        }, ct);
    }

    private async Task ScheduleEligibleTasksCoreAsync(CancellationToken ct)
    {
        if (_track.ClosedAtMs is not null)
        {
            return;
        }

        var mounted = await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
        var driver = _workspaceDrivers.GetRequired(_track.WorkspaceDriver);
        var schedulable = TaskGraphScheduler.ComputeSchedulable(_tasks, driver.SupportsParallelIsolatedWriteLeases);

        foreach (var task in schedulable)
        {
            await StartTaskAttemptCoreAsync(task, driver, mounted, ct).ConfigureAwait(false);
        }
    }

    private async Task StartTaskAttemptCoreAsync(
        TaskDto task,
        IWorkspaceDriver driver,
        MountedWorkspace mounted,
        CancellationToken ct)
    {
        var now = NowMs();
        var attemptId = $"att_{Guid.NewGuid():N}";
        var lease = await driver.AcquireAttemptLeaseAsync(
            mounted,
            attemptId,
            readWrite: task.Access == AccessMode.ReadWrite,
            ct).ConfigureAwait(false);
        var leaseJson = JsonHelpers.ToElement(lease);
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);

        var isCommandTask = string.Equals(task.Kind, "command", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(task.Kind, "cmd", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(task.Kind, "shell", StringComparison.OrdinalIgnoreCase);

        string? subagentSessionId = null;
        if (!isCommandTask)
        {
            subagentSessionId = $"ses_{Guid.NewGuid():N}";
            var subagent = new AgentSessionDto(
                Id: subagentSessionId,
                TrackId: TrackId,
                IsLead: false,
                BoundTaskId: task.Id,
                BoundAttemptId: attemptId,
                Title: $"Task: {task.TaskKey}",
                Provider: string.IsNullOrWhiteSpace(task.Provider) ? GetDefaultProviderCore() : task.Provider!,
                Model: GetDefaultModelCore(),
                NativeThreadId: null,
                Epoch: 1,
                Status: "idle",
                ContextUsageRatio: 0.0,
                ArchivedAtMs: null,
                CreatedAtMs: now,
                UpdatedAtMs: now);

            _sessions[subagent.Id] = subagent;
            _nextTurnIndexBySession[subagent.Id] = 0;
            await PersistAndEmitSessionUpdatedCoreAsync(subagent, now, ct).ConfigureAwait(false);
        }

        var runningTask = task with
        {
            Status = TaskRunStatus.Running,
            StatusDetail = null,
            LatestAttemptId = attemptId,
            UpdatedAtMs = now
        };
        ReplaceTaskInMemoryCore(runningTask);

        var attemptDto = new TaskAttemptDto(
            AttemptId: attemptId,
            TaskId: task.Id,
            TrackId: TrackId,
            SessionId: subagentSessionId,
            Status: "Running",
            WorkerResult: null,
            Meta: JsonHelpers.EmptyObject,
            WorkspaceLease: leaseJson,
            GateVerdict: null,
            StartedAtMs: now,
            FinishedAtMs: null);

        _activeAttempts[attemptId] = new ActiveAttemptState(
            attemptId,
            task.Id,
            subagentSessionId,
            driver,
            lease,
            attemptDto,
            attemptCts);

        await PersistAndEmitTaskUpdatedCoreAsync(runningTask, attemptDto, nextTemplateState: null, now, ct)
            .ConfigureAwait(false);

        if (isCommandTask)
        {
            TrackBackgroundJob(() => RunCommandTaskAttemptInBackgroundAsync(attemptId, runningTask, lease, attemptCts.Token));
        }
        else
        {
            var prompt = BuildContractSubagentPrompt(runningTask);
            TrackBackgroundJob(() => RunSubagentTaskAttemptInBackgroundAsync(attemptId, subagentSessionId!, prompt, attemptCts.Token));
        }
    }

    private async Task RunCommandTaskAttemptInBackgroundAsync(
        string attemptId,
        TaskDto task,
        AttemptWorkspaceLease lease,
        CancellationToken ct)
    {
        try
        {
            var timeoutSecs = task.Gate?.TimeoutSecs ?? _options.DefaultHookTimeoutSecs;
            var cmdRes = await _commandRunner.RunAsync(
                new ShellCommandRequest(task.GoalOrCmd, lease.EffectiveCwd, TimeSpan.FromSeconds(Math.Max(1, timeoutSecs))),
                ct).ConfigureAwait(false);

            var payload = JsonHelpers.ToElement(new
            {
                exitCode = cmdRes.ExitCode,
                stdout = cmdRes.Stdout,
                stderr = cmdRes.Stderr,
                timedOut = cmdRes.TimedOut,
                durationMs = cmdRes.DurationMs
            });

            if (cmdRes.ExitCode == 0 && !cmdRes.TimedOut)
            {
                await CompleteAttemptDoneByAttemptIdAsync(attemptId, payload, ct).ConfigureAwait(false);
            }
            else
            {
                var err = cmdRes.TimedOut
                    ? $"Command timed out after {timeoutSecs}s."
                    : $"Command exited with code {cmdRes.ExitCode}.";
                await CompleteAttemptFailByAttemptIdAsync(attemptId, err, payload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Canceled via governance or track teardown.
        }
        catch (Exception ex)
        {
            try
            {
                await CompleteAttemptFailByAttemptIdAsync(attemptId, ex.Message, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Ignore if already settled.
            }
        }
    }

    private async Task RunSubagentTaskAttemptInBackgroundAsync(
        string attemptId,
        string subagentSessionId,
        string prompt,
        CancellationToken ct)
    {
        try
        {
            var turn = await ExecuteSessionTurnPipelineAsync(subagentSessionId, "wake", prompt, ct).ConfigureAwait(false);
            if (string.Equals(turn.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var stillActive = await EnqueueAsync(_ => Task.FromResult(_activeAttempts.ContainsKey(attemptId)), CancellationToken.None)
                    .ConfigureAwait(false);
                if (stillActive)
                {
                    await CompleteAttemptFailByAttemptIdAsync(
                        attemptId,
                        turn.ErrorText ?? "Contract subagent turn failed.",
                        workerResult: null,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Attempt or session was canceled.
        }
        catch (Exception ex)
        {
            var stillActive = await EnqueueAsync(_ => Task.FromResult(_activeAttempts.ContainsKey(attemptId)), CancellationToken.None)
                .ConfigureAwait(false);
            if (stillActive)
            {
                await CompleteAttemptFailByAttemptIdAsync(attemptId, ex.Message, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<TaskDto> CompleteAttemptDoneByAttemptIdAsync(
        string attemptId,
        JsonElement? workerResult,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var prep = await EnqueueAsync<(TaskDto Task, ActiveAttemptState Active, string? AfterWorkerDoneCmd)>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                if (!_activeAttempts.TryGetValue(attemptId, out var active))
                {
                    throw KernelException.Conflict($"Attempt '{attemptId}' is not active on track '{TrackId}'.");
                }

                var task = RequireTaskCore(active.TaskId);
                var now = NowMs();
                var clonedWorkerResult = workerResult?.Clone();

                var verifyingTask = task with { Status = TaskRunStatus.Verifying, UpdatedAtMs = now };
                ReplaceTaskInMemoryCore(verifyingTask);

                var verifyingAttempt = active.AttemptDto with
                {
                    Status = "Verifying",
                    WorkerResult = clonedWorkerResult
                };
                active.AttemptDto = verifyingAttempt;

                await PersistAndEmitTaskUpdatedCoreAsync(verifyingTask, verifyingAttempt, nextTemplateState: null, now, ct)
                    .ConfigureAwait(false);

                return (verifyingTask, active, _policy?.Manifest.AfterWorkerDoneCmd);
            }, ct).ConfigureAwait(false);

            // 1. Run AfterWorkerDoneCmd (if non-empty) in lease.EffectiveCwd and merge last valid JSON stdout line into attempt.Meta.
            var mergedMeta = prep.Active.AttemptDto.Meta;
            if (!string.IsNullOrWhiteSpace(prep.AfterWorkerDoneCmd))
            {
                var hookResult = await _commandRunner.RunAsync(
                    new ShellCommandRequest(
                        prep.AfterWorkerDoneCmd!,
                        prep.Active.Lease.EffectiveCwd,
                        TimeSpan.FromSeconds(_options.DefaultHookTimeoutSecs)),
                    ct).ConfigureAwait(false);

                mergedMeta = JsonHelpers.MergeLastValidJsonLine(mergedMeta, hookResult.Stdout);
            }

            // 2. Run GateSpecDto steps in lease.EffectiveCwd.
            var gatePassed = true;
            string? failedStepReason = null;
            JsonElement? gateVerdict = null;

            if (prep.Task.Gate is { Steps.Count: > 0 } gateSpec)
            {
                var stepRecords = new List<object>(gateSpec.Steps.Count);
                var stepTimeout = TimeSpan.FromSeconds(Math.Max(1, gateSpec.TimeoutSecs));

                foreach (var step in gateSpec.Steps)
                {
                    var res = await _commandRunner.RunAsync(
                        new ShellCommandRequest(step.Cmd, prep.Active.Lease.EffectiveCwd, stepTimeout),
                        ct).ConfigureAwait(false);

                    stepRecords.Add(new
                    {
                        name = step.Name,
                        cmd = step.Cmd,
                        exitCode = res.ExitCode,
                        stdout = res.Stdout,
                        stderr = res.Stderr,
                        timedOut = res.TimedOut,
                        durationMs = res.DurationMs
                    });

                    if (res.ExitCode != 0 || res.TimedOut)
                    {
                        gatePassed = false;
                        failedStepReason = res.TimedOut
                            ? $"Gate step '{step.Name}' timed out."
                            : $"Gate step '{step.Name}' exited with code {res.ExitCode}.";
                        break;
                    }
                }

                gateVerdict = JsonHelpers.ToElement(new
                {
                    v = 1,
                    passed = gatePassed,
                    failedReason = failedStepReason,
                    steps = stepRecords
                });
            }

            // 3. Settle workspace lease: Promote on pass, Discard on fail!
            var disposition = gatePassed ? AttemptLeaseDisposition.Promote : AttemptLeaseDisposition.Discard;
            await prep.Active.Driver.SettleAttemptLeaseAsync(prep.Active.Lease, disposition, ct).ConfigureAwait(false);

            // 4. Finalize inside actor: OnTaskSettled, persist TaskDto & TaskAttemptDto, schedule unlocked dependents, wake Lead!
            var (finalTask, wakePrompt) = await EnqueueAsync<(TaskDto FinalTask, string WakePrompt)>(async _ =>
            {
                _activeAttempts.Remove(attemptId);
                var currentTask = RequireTaskCore(prep.Task.Id);
                var now = NowMs();

                JsonElement? nextTemplateState = null;
                string? policyNote = null;

                if (_policy is not null)
                {
                    var settledCtx = new PolicyTaskSettledContext(
                        TrackId: TrackId,
                        TaskKey: currentTask.TaskKey,
                        AccessMode: FormatAccessModeForPolicy(currentTask.Access),
                        AttemptStatus: gatePassed ? "done" : "failed",
                        WorkerResult: prep.Active.AttemptDto.WorkerResult,
                        AttemptMeta: mergedMeta,
                        GateVerdict: gateVerdict,
                        Inputs: _track.TemplateInputs);

                    var eval = _policyVm.OnTaskSettled(_policy, settledCtx, _track.TemplateState);
                    _track = _track with { TemplateState = eval.NextState };
                    nextTemplateState = eval.NextState;
                    policyNote = eval.Output;
                }

                var settledTask = currentTask with
                {
                    Status = gatePassed ? TaskRunStatus.Done : TaskRunStatus.Failed,
                    StatusDetail = gatePassed ? null : failedStepReason,
                    UpdatedAtMs = now
                };
                ReplaceTaskInMemoryCore(settledTask);

                var settledAttempt = prep.Active.AttemptDto with
                {
                    Status = gatePassed ? "Done" : "Failed",
                    Meta = mergedMeta,
                    GateVerdict = gateVerdict,
                    FinishedAtMs = now
                };

                await PersistAndEmitTaskUpdatedCoreAsync(settledTask, settledAttempt, nextTemplateState, now, ct)
                    .ConfigureAwait(false);

                await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);

                var summarySb = new StringBuilder();
                summarySb.Append($"Task '{settledTask.TaskKey}' (attempt '{attemptId}') settled as {settledTask.Status}.");
                if (!string.IsNullOrWhiteSpace(failedStepReason))
                {
                    summarySb.Append($" Reason: {failedStepReason}");
                }

                if (!string.IsNullOrWhiteSpace(policyNote))
                {
                    summarySb.Append('\n').Append(policyNote);
                }

                return (settledTask, summarySb.ToString());
            }, ct).ConfigureAwait(false);

            await WakeLeadAsync("wake", wakePrompt, ct).ConfigureAwait(false);
            return finalTask;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private async Task<TaskDto> CompleteAttemptFailByAttemptIdAsync(
        string attemptId,
        string reason,
        JsonElement? workerResult,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var active = await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                if (!_activeAttempts.TryGetValue(attemptId, out var state))
                {
                    throw KernelException.Conflict($"Attempt '{attemptId}' is not active on track '{TrackId}'.");
                }

                return state;
            }, ct).ConfigureAwait(false);

            await active.Driver.SettleAttemptLeaseAsync(active.Lease, AttemptLeaseDisposition.Discard, ct)
                .ConfigureAwait(false);

            var (failedTask, wakePrompt) = await EnqueueAsync<(TaskDto FailedTask, string WakePrompt)>(async _ =>
            {
                _activeAttempts.Remove(attemptId);
                var currentTask = RequireTaskCore(active.TaskId);
                var now = NowMs();
                var clonedWorkerResult = workerResult?.Clone();

                JsonElement? nextTemplateState = null;
                string? policyNote = null;

                if (_policy is not null)
                {
                    var settledCtx = new PolicyTaskSettledContext(
                        TrackId: TrackId,
                        TaskKey: currentTask.TaskKey,
                        AccessMode: FormatAccessModeForPolicy(currentTask.Access),
                        AttemptStatus: "failed",
                        WorkerResult: clonedWorkerResult,
                        AttemptMeta: active.AttemptDto.Meta,
                        GateVerdict: null,
                        Inputs: _track.TemplateInputs);

                    var eval = _policyVm.OnTaskSettled(_policy, settledCtx, _track.TemplateState);
                    _track = _track with { TemplateState = eval.NextState };
                    nextTemplateState = eval.NextState;
                    policyNote = eval.Output;
                }

                var updatedTask = currentTask with
                {
                    Status = TaskRunStatus.Failed,
                    StatusDetail = reason,
                    UpdatedAtMs = now
                };
                ReplaceTaskInMemoryCore(updatedTask);

                var updatedAttempt = active.AttemptDto with
                {
                    Status = "Failed",
                    WorkerResult = clonedWorkerResult,
                    FinishedAtMs = now
                };

                await PersistAndEmitTaskUpdatedCoreAsync(updatedTask, updatedAttempt, nextTemplateState, now, ct)
                    .ConfigureAwait(false);
                await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);

                var msg = string.IsNullOrWhiteSpace(policyNote)
                    ? $"Task '{updatedTask.TaskKey}' (attempt '{attemptId}') failed: {reason}"
                    : $"Task '{updatedTask.TaskKey}' (attempt '{attemptId}') failed: {reason}\n{policyNote}";
                return (updatedTask, msg);
            }, ct).ConfigureAwait(false);

            await WakeLeadAsync("wake", wakePrompt, ct).ConfigureAwait(false);
            return failedTask;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private async Task<SessionTurnDto> ExecuteSessionTurnPipelineAsync(
        string sessionId,
        string triggerKind,
        string prompt,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            // Pre-turn auto-compaction check if session hydrated at >= 0.80
            var needsPreCompact = await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var s = RequireSessionCore(sessionId);
                return s.ContextUsageRatio >= _options.AutoCompactionThreshold;
            }, ct).ConfigureAwait(false);

            if (needsPreCompact)
            {
                await CompactSessionInternalAsync(sessionId, ct).ConfigureAwait(false);
            }

            var turnSetup = await EnqueueAsync<(ActiveTurnState ActiveState, string EffectivePrompt)>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var session = RequireSessionCore(sessionId);
                var agentSession = await GetOrOpenAgentSessionCoreAsync(session, overrideSystemPrompt: null, ct)
                    .ConfigureAwait(false);

                var effectivePrompt = prompt;
                if (_policy is not null)
                {
                    var turnCtx = new PolicyTurnContext(
                        TrackId: TrackId,
                        SessionId: session.Id,
                        IsLead: session.IsLead,
                        BoundAttemptId: session.BoundAttemptId,
                        ContextUsageRatio: session.ContextUsageRatio,
                        Inputs: _track.TemplateInputs);
                    var eval = _policyVm.BuildTurnContext(_policy, turnCtx, _track.TemplateState);
                    await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(eval.Output))
                    {
                        effectivePrompt = $"<policy_context>\n{eval.Output}\n</policy_context>\n\n{prompt}";
                    }
                }

                var now = NowMs();
                var turnIndex = NextTurnIndexCore(session.Id);
                var turnId = $"trn_{turnIndex:D6}_{Guid.NewGuid():N}";
                var inputJson = JsonHelpers.ToElement(new { prompt });

                var turnDto = new SessionTurnDto(
                    Id: turnId,
                    SessionId: session.Id,
                    TurnIndex: turnIndex,
                    Epoch: session.Epoch,
                    TriggerKind: triggerKind,
                    Input: inputJson,
                    Status: "running",
                    Usage: null,
                    ErrorText: null,
                    StartedAtMs: now,
                    CompletedAtMs: null);

                var runningSession = session with { Status = "running", UpdatedAtMs = now };
                _sessions[session.Id] = runningSession;
                await PersistAndEmitSessionUpdatedCoreAsync(runningSession, now, ct).ConfigureAwait(false);

                var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _actorShutdownCts.Token);
                var activeState = new ActiveTurnState(session.Id, turnDto, agentSession, turnCts);
                _activeTurns[session.Id] = activeState;
                return (activeState, effectivePrompt);
            }, ct).ConfigureAwait(false);

            var activeTurn = turnSetup.ActiveState;
            var items = new List<SessionItemDto>();
            var textAccum = new StringBuilder();
            var thinkingAccum = new StringBuilder();
            TokenUsage? finalUsage = null;
            string? boundThreadId = null;
            string? errorText = null;
            var turnStatus = "completed";

            items.Add(new SessionItemDto(
                Id: 0,
                SessionId: sessionId,
                TurnId: activeTurn.TurnDto.Id,
                Kind: "user",
                Payload: JsonHelpers.ToElement(new { v = 1, content = prompt, triggerKind }),
                CreatedAtMs: activeTurn.TurnDto.StartedAtMs));

            try
            {
                await foreach (var evt in activeTurn.AgentSession
                                   .StreamTurnAsync(new TurnInput(turnSetup.EffectivePrompt, activeTurn.TurnDto.Id), activeTurn.TurnCts.Token)
                                   .ConfigureAwait(false))
                {
                    var evtNow = NowMs();
                    switch (evt)
                    {
                        case UnifiedAgentEvent.ThreadBound tb:
                            boundThreadId = tb.NativeThreadId;
                            break;

                        case UnifiedAgentEvent.TextDelta td:
                            textAccum.Append(td.Delta);
                            _eventHub.PublishTransient(
                                _track.AreaId,
                                TrackId,
                                KernelEventKinds.SessionTextDelta,
                                JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, delta = td.Delta }),
                                evtNow);
                            break;

                        case UnifiedAgentEvent.ThinkingDelta thd:
                            thinkingAccum.Append(thd.Delta);
                            _eventHub.PublishTransient(
                                _track.AreaId,
                                TrackId,
                                KernelEventKinds.SessionThinkingDelta,
                                JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, delta = thd.Delta }),
                                evtNow);
                            break;

                        case UnifiedAgentEvent.ToolCallStarted tcs:
                            _eventHub.PublishTransient(
                                _track.AreaId,
                                TrackId,
                                KernelEventKinds.SessionToolProgress,
                                JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, phase = "started", callId = tcs.CallId, name = tcs.Name, args = tcs.Args }),
                                evtNow);
                            items.Add(new SessionItemDto(
                                Id: 0,
                                SessionId: sessionId,
                                TurnId: activeTurn.TurnDto.Id,
                                Kind: "tool_call",
                                Payload: JsonHelpers.ToElement(new { v = 1, callId = tcs.CallId, name = tcs.Name, args = tcs.Args }),
                                CreatedAtMs: evtNow));
                            break;

                        case UnifiedAgentEvent.ToolCallCompleted tcc:
                            _eventHub.PublishTransient(
                                _track.AreaId,
                                TrackId,
                                KernelEventKinds.SessionToolProgress,
                                JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, phase = "completed", callId = tcc.CallId, isError = tcc.IsError }),
                                evtNow);
                            items.Add(new SessionItemDto(
                                Id: 0,
                                SessionId: sessionId,
                                TurnId: activeTurn.TurnDto.Id,
                                Kind: "tool_result",
                                Payload: JsonHelpers.ToElement(new { v = 1, callId = tcc.CallId, output = tcc.Output, isError = tcc.IsError }),
                                CreatedAtMs: evtNow));
                            break;

                        case UnifiedAgentEvent.ApprovalRequested approval:
                            var options = new[] { "Approve", "Deny" };
                            var suspension = await EnqueueAsync(async _ =>
                            {
                                var susp = await CreateAskSuspensionCoreAsync(
                                    $"Approve tool '{approval.ToolName}'?",
                                    options,
                                    sessionId,
                                    activeTurn.TurnCts.Token).ConfigureAwait(false);
                                activeTurn.OwnedAskIds.Add(susp.Ask.Id);
                                return susp;
                            }, activeTurn.TurnCts.Token).ConfigureAwait(false);

                            if (suspension.ShouldWakeLead)
                            {
                                await DispatchLeadWakePromptIfNeededAsync("wake", suspension.LeadWakePrompt!, activeTurn.TurnCts.Token)
                                    .ConfigureAwait(false);
                            }

                            var humanAns = await suspension.WaiterTask.WaitAsync(activeTurn.TurnCts.Token).ConfigureAwait(false);
                            var approved = AskAnswerEvaluator.IsApproved(humanAns, options);
                            await activeTurn.AgentSession.RespondApprovalAsync(
                                approval.RequestId,
                                approved,
                                approved ? null : "Denied by user.",
                                activeTurn.TurnCts.Token).ConfigureAwait(false);
                            break;

                        case UnifiedAgentEvent.TurnCompleted tc:
                            finalUsage = tc.Usage;
                            break;

                        case UnifiedAgentEvent.TurnError te:
                            turnStatus = "failed";
                            errorText = $"{te.Code}: {te.Message}";
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (activeTurn.Interrupted || _actorShutdownCts.IsCancellationRequested)
            {
                turnStatus = "interrupted";
                errorText = "Turn interrupted.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                turnStatus = "interrupted";
                errorText = "Caller canceled turn.";
                throw;
            }
            catch (Exception ex)
            {
                turnStatus = "failed";
                errorText = ex.Message;
            }
            finally
            {
                activeTurn.TurnCts.Dispose();
            }

            var endNow = NowMs();
            if (thinkingAccum.Length > 0)
            {
                items.Add(new SessionItemDto(
                    Id: 0,
                    SessionId: sessionId,
                    TurnId: activeTurn.TurnDto.Id,
                    Kind: "thinking",
                    Payload: JsonHelpers.ToElement(new { v = 1, text = thinkingAccum.ToString() }),
                    CreatedAtMs: endNow));
            }

            if (textAccum.Length > 0)
            {
                items.Add(new SessionItemDto(
                    Id: 0,
                    SessionId: sessionId,
                    TurnId: activeTurn.TurnDto.Id,
                    Kind: "assistant_text",
                    Payload: JsonHelpers.ToElement(new { v = 1, text = textAccum.ToString() }),
                    CreatedAtMs: endNow));
            }

            var finalizeOutcome = await EnqueueAsync<(SessionTurnDto CompletedTurn, bool ShouldAutoCompact, (string TriggerKind, string Prompt)? NextQueuedWake)>(async _ =>
            {
                _activeTurns.Remove(sessionId);
                var currentSession = RequireSessionCore(sessionId);

                var nextRatio = currentSession.ContextUsageRatio;
                if (finalUsage is { ContextWindowTokens: > 0 } usage)
                {
                    nextRatio = Math.Clamp(
                        (double)(usage.InputTokens + usage.OutputTokens) / usage.ContextWindowTokens.Value,
                        0.0,
                        1.0);
                }

                var updatedSession = currentSession with
                {
                    NativeThreadId = boundThreadId ?? activeTurn.AgentSession.NativeThreadId ?? currentSession.NativeThreadId,
                    Status = "idle",
                    ContextUsageRatio = nextRatio,
                    UpdatedAtMs = endNow
                };
                _sessions[sessionId] = updatedSession;

                var completedTurn = activeTurn.TurnDto with
                {
                    Status = turnStatus,
                    Usage = finalUsage is null ? null : JsonHelpers.ToElement(finalUsage),
                    ErrorText = errorText,
                    CompletedAtMs = endNow
                };

                await _eventHub.EnsureInitializedFromStoreAsync(_store, CancellationToken.None).ConfigureAwait(false);
                await _store.RecordTurnAndItemsAsync(completedTurn, items, updatedSession, CancellationToken.None)
                    .ConfigureAwait(false);

                var turnEvt = new EventEnvelopeDto(
                    EventId: null,
                    AreaId: _track.AreaId,
                    TrackId: TrackId,
                    Kind: KernelEventKinds.TurnCompleted,
                    Payload: JsonHelpers.ToElement(completedTurn),
                    CreatedAtMs: endNow);
                await _store.AppendEventAsync(turnEvt, CancellationToken.None).ConfigureAwait(false);
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, turnEvt, CancellationToken.None).ConfigureAwait(false);

                var sessEvt = new EventEnvelopeDto(
                    EventId: null,
                    AreaId: _track.AreaId,
                    TrackId: TrackId,
                    Kind: KernelEventKinds.SessionUpdated,
                    Payload: JsonHelpers.ToElement(updatedSession),
                    CreatedAtMs: endNow);
                await _store.AppendEventAsync(sessEvt, CancellationToken.None).ConfigureAwait(false);
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, sessEvt, CancellationToken.None).ConfigureAwait(false);

                var autoCompact = string.Equals(turnStatus, "completed", StringComparison.Ordinal) &&
                                  nextRatio >= _options.AutoCompactionThreshold;

                (string TriggerKind, string Prompt)? nextWake = null;
                if (_pendingWakes.TryGetValue(sessionId, out var q) && q.Count > 0)
                {
                    nextWake = q.Dequeue();
                }

                return (completedTurn, autoCompact, nextWake);
            }, CancellationToken.None).ConfigureAwait(false);

            if (finalizeOutcome.ShouldAutoCompact)
            {
                await CompactSessionInternalAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }

            if (finalizeOutcome.NextQueuedWake is { } queued)
            {
                TrackBackgroundJob(() => ExecuteSessionTurnPipelineAsync(sessionId, queued.TriggerKind, queued.Prompt, _actorShutdownCts.Token));
            }

            return finalizeOutcome.CompletedTurn;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private async Task<AgentSessionDto> CompactSessionInternalAsync(string sessionId, CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var prep = await EnqueueAsync<(AgentSessionDto Session, IAgentSession OldAgentSession, int HandoverTurnIndex)>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var session = RequireSessionCore(sessionId);
                if (_activeTurns.ContainsKey(sessionId))
                {
                    throw KernelException.Conflict($"Cannot compact session '{sessionId}' while a turn is actively running.");
                }

                var oldAgentSession = await GetOrOpenAgentSessionCoreAsync(session, overrideSystemPrompt: null, ct)
                    .ConfigureAwait(false);
                var turnIdx = NextTurnIndexCore(sessionId);
                return (session, oldAgentSession, turnIdx);
            }, ct).ConfigureAwait(false);

            // 1. Request structured handover summary turn on current IAgentSession (NEVER native CLI /compact!).
            const string handoverPrompt =
                "Epoch handover requested. Summarize concisely in structured sections: " +
                "(1) Current Progress, (2) Key Paths & Artifacts, (3) Verified Conclusions, and (4) Immediate Next Steps.";

            var handoverTurnId = $"trn_{prep.HandoverTurnIndex:D6}_{Guid.NewGuid():N}";
            var startedAt = NowMs();
            var summaryBuilder = new StringBuilder();
            TokenUsage? handoverUsage = null;

            await foreach (var evt in prep.OldAgentSession
                               .StreamTurnAsync(new TurnInput(handoverPrompt, handoverTurnId), ct)
                               .ConfigureAwait(false))
            {
                switch (evt)
                {
                    case UnifiedAgentEvent.TextDelta td:
                        summaryBuilder.Append(td.Delta);
                        break;
                    case UnifiedAgentEvent.TurnCompleted tc:
                        handoverUsage = tc.Usage;
                        break;
                }
            }

            var handoverSummary = summaryBuilder.ToString().Trim();

            // 2. Dispose old IAgentSession instance.
            await prep.OldAgentSession.DisposeAsync().ConfigureAwait(false);

            // 3 & 4. Update Epoch = Epoch + 1, NativeThreadId = null, ContextUsageRatio = 0.0 -> seed fresh IAgentSession with latest Report + handover summary!
            return await EnqueueAsync(async _ =>
            {
                _openAgentSessions.Remove(sessionId);
                var current = RequireSessionCore(sessionId);
                var now = NowMs();

                var handoverTurn = new SessionTurnDto(
                    Id: handoverTurnId,
                    SessionId: sessionId,
                    TurnIndex: prep.HandoverTurnIndex,
                    Epoch: current.Epoch,
                    TriggerKind: "system",
                    Input: JsonHelpers.ToElement(new { prompt = handoverPrompt, kind = "epoch_handover" }),
                    Status: "completed",
                    Usage: handoverUsage is null ? null : JsonHelpers.ToElement(handoverUsage),
                    ErrorText: null,
                    StartedAtMs: startedAt,
                    CompletedAtMs: now);

                var handoverItems = new List<SessionItemDto>
                {
                    new(0, sessionId, handoverTurnId, "user", JsonHelpers.ToElement(new { v = 1, content = handoverPrompt }), startedAt),
                    new(0, sessionId, handoverTurnId, "assistant_text", JsonHelpers.ToElement(new { v = 1, text = handoverSummary }), now)
                };

                var compacted = current with
                {
                    Epoch = current.Epoch + 1,
                    NativeThreadId = null,
                    ContextUsageRatio = 0.0,
                    Status = "idle",
                    UpdatedAtMs = now
                };
                _sessions[sessionId] = compacted;

                await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
                await _store.RecordTurnAndItemsAsync(handoverTurn, handoverItems, compacted, ct).ConfigureAwait(false);
                await PersistAndEmitSessionUpdatedCoreAsync(compacted, now, ct).ConfigureAwait(false);

                var latestReportMarkdown = _reportEngine.RenderMarkdown(_report, withMarkers: true);
                var seedPrompt = BuildEpochSeedSystemPrompt(compacted, latestReportMarkdown, handoverSummary);

                // Seed fresh IAgentSession with ResumeNativeThreadId == null and SystemPrompt = Report + Handover Summary.
                var freshAgentSession = await OpenFreshAgentSessionCoreAsync(compacted, seedPrompt, ct).ConfigureAwait(false);
                _openAgentSessions[sessionId] = freshAgentSession;

                return compacted;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private async Task<IAgentSession> GetOrOpenAgentSessionCoreAsync(
        AgentSessionDto session,
        string? overrideSystemPrompt,
        CancellationToken ct)
    {
        if (_openAgentSessions.TryGetValue(session.Id, out var existing))
        {
            return existing;
        }

        var opened = await OpenFreshAgentSessionCoreAsync(session, overrideSystemPrompt, ct).ConfigureAwait(false);
        _openAgentSessions[session.Id] = opened;
        return opened;
    }

    private async Task<IAgentSession> OpenFreshAgentSessionCoreAsync(
        AgentSessionDto session,
        string? overrideSystemPrompt,
        CancellationToken ct)
    {
        var mounted = await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
        var effectiveCwd = mounted.MergedCwd;
        var overlayMounts = MapOverlayLayers(mounted.ProcessOverlayLayers);
        var sandboxMode = session.IsLead
            ? _options.LeadSandboxMode
            : _options.FreeSubagentSandboxMode;

        if (session.BoundAttemptId is not null)
        {
            if (_activeAttempts.TryGetValue(session.BoundAttemptId, out var activeAttempt))
            {
                effectiveCwd = activeAttempt.Lease.EffectiveCwd;
                overlayMounts = MapOverlayLayers(activeAttempt.Lease.ProcessOverlayLayers ?? mounted.ProcessOverlayLayers);
            }

            if (session.BoundTaskId is not null)
            {
                var boundTask = _tasks.FirstOrDefault(t => string.Equals(t.Id, session.BoundTaskId, StringComparison.Ordinal));
                if (boundTask is not null)
                {
                    sandboxMode = boundTask.Access == AccessMode.ReadOnly
                        ? _options.ReadOnlyTaskSandboxMode
                        : _options.ReadWriteTaskSandboxMode;
                }
            }
        }

        var mcpReg = _mcpBridge.RegisterSession(
            TrackId,
            session.Id,
            session.IsLead,
            session.BoundTaskId,
            session.BoundAttemptId,
            _track.EnabledPlugins);

        var systemPrompt = overrideSystemPrompt ?? BuildDefaultSystemPromptCore(session);
        var launchSpec = new HarnessLaunchSpec(
            Provider: session.Provider,
            Model: session.Model,
            Cwd: effectiveCwd,
            ResumeNativeThreadId: session.NativeThreadId,
            SystemPrompt: systemPrompt,
            McpServers: mcpReg.McpServers,
            Sandbox: sandboxMode,
            OverlayMounts: overlayMounts);

        return await _agentRuntime.OpenSessionAsync(launchSpec, ct).ConfigureAwait(false);
    }

    private async Task DispatchLeadWakePromptIfNeededAsync(string triggerKind, string wakePrompt, CancellationToken ct)
    {
        var check = await EnqueueAsync<(string? LeadSessionId, bool QueuedBecauseBusy)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_track.ClosedAtMs is not null || string.IsNullOrEmpty(_track.LeadSessionId))
            {
                return (null, false);
            }

            var leadId = _track.LeadSessionId!;
            if (!_sessions.ContainsKey(leadId))
            {
                return (null, false);
            }

            if (_activeTurns.ContainsKey(leadId))
            {
                EnqueuePendingWakeCore(leadId, triggerKind, wakePrompt);
                return (leadId, true);
            }

            return (leadId, false);
        }, ct).ConfigureAwait(false);

        if (check.LeadSessionId is null || check.QueuedBecauseBusy)
        {
            return;
        }

        if (_options.DispatchWakesInBackground)
        {
            TrackBackgroundJob(() => ExecuteSessionTurnPipelineAsync(check.LeadSessionId, triggerKind, wakePrompt, _actorShutdownCts.Token));
        }
        else
        {
            await ExecuteSessionTurnPipelineAsync(check.LeadSessionId, triggerKind, wakePrompt, ct).ConfigureAwait(false);
        }
    }

    private void EnqueuePendingWakeCore(string sessionId, string triggerKind, string prompt)
    {
        if (!_pendingWakes.TryGetValue(sessionId, out var queue))
        {
            queue = new Queue<(string TriggerKind, string Prompt)>();
            _pendingWakes[sessionId] = queue;
        }

        queue.Enqueue((triggerKind, prompt));
    }

    private async Task SwitchLeadSessionCoreAsync(string newLeadSessionId, long now, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(newLeadSessionId, out var target))
        {
            throw KernelException.NotFound($"Session '{newLeadSessionId}' does not exist on track '{TrackId}'.");
        }

        if (target.BoundAttemptId is not null)
        {
            throw KernelException.Forbidden(
                $"Contract subagent session '{newLeadSessionId}' bound to attempt '{target.BoundAttemptId}' cannot become Lead.");
        }

        var oldLeadId = _track.LeadSessionId;
        if (oldLeadId is not null && _sessions.TryGetValue(oldLeadId, out var oldLead) && oldLead.IsLead)
        {
            var demoted = oldLead with { IsLead = false, UpdatedAtMs = now };
            _sessions[oldLeadId] = demoted;
            await PersistAndEmitSessionUpdatedCoreAsync(demoted, now, ct).ConfigureAwait(false);
        }

        var promoted = target with { IsLead = true, UpdatedAtMs = now };
        _sessions[newLeadSessionId] = promoted;
        _track = _track with { LeadSessionId = newLeadSessionId, UpdatedAtMs = now };
        _mcpBridge.UpdateLeadStatus(TrackId, newLeadSessionId);
        await PersistAndEmitSessionUpdatedCoreAsync(promoted, now, ct).ConfigureAwait(false);
    }

    private async Task<PendingAskSuspension> CreateAskSuspensionCoreAsync(
        string title,
        IReadOnlyList<string> options,
        string? callerSessionId,
        CancellationToken ct)
    {
        var effectiveOptions = options.Count > 0 ? options : ["Approve", "Deny"];
        var questions = new[] { new AskQuestionDto(title, effectiveOptions, IsMultiSelect: false) };
        return await CreateAskSuspensionFromQuestionsCoreAsync(questions, callerSessionId, ct).ConfigureAwait(false);
    }

    private async Task<PendingAskSuspension> CreateAskSuspensionFromQuestionsCoreAsync(
        IReadOnlyList<AskQuestionDto> questions,
        string? callerSessionId,
        CancellationToken ct)
    {
        var now = NowMs();
        var ask = new AskDto(
            Id: $"ask_{Guid.NewGuid():N}",
            TrackId: TrackId,
            Status: AskStatus.Open,
            Questions: questions,
            Answer: null,
            CreatedAtMs: now,
            AnsweredAtMs: null);

        _asks[ask.Id] = ask;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAskWaiters[ask.Id] = tcs;

        var evt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.AskChanged,
            Payload: JsonHelpers.ToElement(ask),
            CreatedAtMs: now);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpsertAskAsync(ask, evt, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);

        var options = questions[0].Options.Count > 0 ? questions[0].Options : ["Approve", "Deny"];
        var callerIsLead = callerSessionId is not null &&
                           string.Equals(callerSessionId, _track.LeadSessionId, StringComparison.Ordinal);

        // If a non-lead session initiated an ask, notify Lead only if desired? No, Ask is for human UI; Lead wakes when answered!
        _ = callerIsLead;
        return new PendingAskSuspension(ask, options, tcs.Task, ShouldWakeLead: false, LeadWakePrompt: null);
    }

    private async Task<TaskAttemptDto> CancelAndDiscardActiveAttemptCoreAsync(
        ActiveAttemptState active,
        string reason,
        long now,
        CancellationToken ct)
    {
        _activeAttempts.Remove(active.AttemptId);
        active.Cts.Cancel();

        if (active.SessionId is not null && _activeTurns.TryGetValue(active.SessionId, out var runningTurn))
        {
            runningTurn.Interrupted = true;
            runningTurn.TurnCts.Cancel();
            try
            {
                await runningTurn.AgentSession.CancelTurnAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort turn cancellation.
            }
        }

        await active.Driver.SettleAttemptLeaseAsync(active.Lease, AttemptLeaseDisposition.Discard, ct)
            .ConfigureAwait(false);

        active.Cts.Dispose();
        _ = reason;

        return active.AttemptDto with
        {
            Status = "Canceled",
            FinishedAtMs = now
        };
    }

    private async Task CancelAllInFlightAttemptsCoreAsync(string reason, CancellationToken ct)
    {
        var now = NowMs();
        foreach (var active in _activeAttempts.Values.ToArray())
        {
            var canceledAttempt = await CancelAndDiscardActiveAttemptCoreAsync(active, reason, now, ct)
                .ConfigureAwait(false);

            var task = _tasks.FirstOrDefault(t => string.Equals(t.Id, active.TaskId, StringComparison.Ordinal));
            if (task is not null)
            {
                var canceledTask = task with
                {
                    Status = TaskRunStatus.Canceled,
                    Ready = false,
                    StatusDetail = reason,
                    UpdatedAtMs = now
                };
                ReplaceTaskInMemoryCore(canceledTask);
                await PersistAndEmitTaskUpdatedCoreAsync(canceledTask, canceledAttempt, nextTemplateState: null, now, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private void ValidateManifestReportRulesCore(
        string editorRole,
        IReadOnlyList<string> touchedSections,
        int nextProseChars)
    {
        if (_policy is null)
        {
            return;
        }

        if (_policy.Manifest.MaxProseChars is { } maxChars && nextProseChars > maxChars)
        {
            throw KernelException.PolicyDenied(
                $"Report prose length ({nextProseChars} chars) exceeds template maxProseChars ({maxChars}).");
        }

        if (_policy.Manifest.Sections.Count > 0 && touchedSections.Count > 0)
        {
            foreach (var touched in touchedSections)
            {
                var rule = _policy.Manifest.Sections.FirstOrDefault(s =>
                    string.Equals(s.H1.TrimStart('#').Trim(), touched.TrimStart('#').Trim(), StringComparison.OrdinalIgnoreCase));

                if (rule?.EditableBy is { Count: > 0 } allowed &&
                    !allowed.Any(r => string.Equals(r, editorRole, StringComparison.OrdinalIgnoreCase)))
                {
                    throw KernelException.PolicyDenied(
                        $"Section '{rule.H1}' is not editable by role '{editorRole}'.");
                }
            }
        }
    }

    private static IReadOnlyList<string> CollectTouchedSections(
        ReportDocument prev,
        ReportCommitResult commit,
        IReadOnlyList<BlockOp> ops)
    {
        var sections = new HashSet<string>(StringComparer.Ordinal);
        var prevById = prev.Blocks.ToDictionary(b => b.Id, StringComparer.Ordinal);
        var nextById = commit.NextDocument.Blocks.ToDictionary(b => b.Id, StringComparer.Ordinal);

        foreach (var op in ops)
        {
            if (op is BlockOp.ReplaceSectionOp rs && !string.IsNullOrWhiteSpace(rs.SectionH1))
            {
                sections.Add(rs.SectionH1);
            }
        }

        foreach (var change in commit.Changes)
        {
            if (prevById.TryGetValue(change.Id, out var p) && !string.IsNullOrWhiteSpace(p.SectionH1))
            {
                sections.Add(p.SectionH1!);
            }

            if (nextById.TryGetValue(change.Id, out var n) && !string.IsNullOrWhiteSpace(n.SectionH1))
            {
                sections.Add(n.SectionH1!);
            }
        }

        return sections.ToArray();
    }

    private (string EditorRole, string? BoundAttemptId) ResolveEditorIdentityCore(
        EditActorKind? actorKind,
        string? callerSessionId)
    {
        if (actorKind == EditActorKind.Human)
        {
            return ("human", null);
        }

        if (actorKind == EditActorKind.System)
        {
            return ("system", null);
        }

        if (!string.IsNullOrEmpty(callerSessionId))
        {
            var session = RequireSessionCore(callerSessionId);
            return (session.IsLead ? "lead" : "subagent", session.BoundAttemptId);
        }

        return ("system", null);
    }

    private AgentSessionDto RequireSessionCore(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw KernelException.NotFound($"Session '{sessionId}' not found on track '{TrackId}'.");
        }

        return session;
    }

    private AgentSessionDto RequireGovernanceSessionCore(string callerSessionId, string toolName)
    {
        var session = RequireSessionCore(callerSessionId);
        if (!string.IsNullOrEmpty(session.BoundAttemptId))
        {
            throw KernelException.Forbidden(
                $"Governance tool '{toolName}' requires BoundAttemptId == null, but session '{callerSessionId}' is bound to attempt '{session.BoundAttemptId}'.");
        }

        return session;
    }

    private AgentSessionDto RequireBoundAttemptSessionCore(string callerSessionId, string toolName)
    {
        var session = RequireSessionCore(callerSessionId);
        if (string.IsNullOrEmpty(session.BoundAttemptId))
        {
            throw KernelException.Forbidden(
                $"Completion tool '{toolName}' requires BoundAttemptId != null, but session '{callerSessionId}' is unbound.");
        }

        return session;
    }

    private TaskDto RequireTaskCore(string taskKeyOrId)
    {
        var task = _tasks.FirstOrDefault(t =>
                       string.Equals(t.TaskKey, taskKeyOrId, StringComparison.Ordinal) ||
                       string.Equals(t.Id, taskKeyOrId, StringComparison.Ordinal))
                   ?? throw KernelException.NotFound($"Task '{taskKeyOrId}' not found on track '{TrackId}'.");
        return task;
    }

    private void ReplaceTaskInMemoryCore(TaskDto updated)
    {
        for (var i = 0; i < _tasks.Count; i++)
        {
            if (string.Equals(_tasks[i].Id, updated.Id, StringComparison.Ordinal))
            {
                _tasks[i] = updated;
                return;
            }
        }

        _tasks.Add(updated);
    }

    private async Task UpdateTemplateStateIfChangedCoreAsync(JsonElement nextState, CancellationToken ct)
    {
        if (nextState.ValueKind == JsonValueKind.Undefined || JsonElement.DeepEquals(_track.TemplateState, nextState))
        {
            return;
        }

        _track = _track with { TemplateState = nextState.Clone(), UpdatedAtMs = NowMs() };
        await _store.UpdateTrackAndEmitAsync(_track, evt: null, ct).ConfigureAwait(false);
    }

    private async Task EmitTrackUpdatedCoreAsync(long now, CancellationToken ct)
    {
        var evt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.TrackUpdated,
            Payload: JsonHelpers.ToElement(_track),
            CreatedAtMs: now);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpdateTrackAndEmitAsync(_track, evt, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
    }

    private async Task PersistAndEmitSessionUpdatedCoreAsync(AgentSessionDto session, long now, CancellationToken ct)
    {
        var evt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.SessionUpdated,
            Payload: JsonHelpers.ToElement(session),
            CreatedAtMs: now);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpsertSessionAsync(session, evt, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
    }

    private async Task PersistAndEmitTaskUpdatedCoreAsync(
        TaskDto task,
        TaskAttemptDto? attempt,
        JsonElement? nextTemplateState,
        long now,
        CancellationToken ct)
    {
        var evt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.TaskUpdated,
            Payload: JsonHelpers.ToElement(new { task, attempt }),
            CreatedAtMs: now);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpsertTaskAndAttemptAsync(task, attempt, nextTemplateState, evt, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
    }

    private async Task PersistAndEmitTerminalChangedCoreAsync(TerminalDto terminal, long now, CancellationToken ct)
    {
        var evt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.TerminalChanged,
            Payload: JsonHelpers.ToElement(terminal),
            CreatedAtMs: now);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpsertTerminalAsync(terminal, evt, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
    }

    private TrackAggregateSnapshot BuildSnapshotCore()
    {
        return new TrackAggregateSnapshot(
            Track: _track,
            Report: _report,
            Tasks: _tasks.ToArray(),
            Sessions: OrderedSessionsCore(),
            OpenAsks: _asks.Values.Where(a => a.Status == AskStatus.Open).OrderBy(a => a.CreatedAtMs).ToArray(),
            TemplateSnapshotSource: _templateSnapshotSource);
    }

    private IReadOnlyList<AgentSessionDto> OrderedSessionsCore()
    {
        return _sessions.Values
            .OrderByDescending(s => s.IsLead)
            .ThenBy(s => s.CreatedAtMs)
            .ToArray();
    }

    private string GetDefaultProviderCore()
    {
        if (_track.LeadSessionId is { } leadId && _sessions.TryGetValue(leadId, out var lead))
        {
            return lead.Provider;
        }

        return _options.DefaultProvider;
    }

    private string? GetDefaultModelCore()
    {
        if (_track.LeadSessionId is { } leadId && _sessions.TryGetValue(leadId, out var lead))
        {
            return lead.Model;
        }

        return _options.DefaultModel;
    }

    private int NextTurnIndexCore(string sessionId)
    {
        _nextTurnIndexBySession.TryGetValue(sessionId, out var current);
        var next = current + 1;
        _nextTurnIndexBySession[sessionId] = next;
        return next;
    }

    private static int ComputeMaxTurnIndex(IReadOnlyList<SessionItemDto> items)
    {
        var maxIdx = 0;
        var distinctTurns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            distinctTurns.Add(item.TurnId);
            if (item.TurnId.StartsWith("trn_", StringComparison.Ordinal))
            {
                var rest = item.TurnId["trn_".Length..];
                var underscore = rest.IndexOf('_');
                var numSpan = underscore > 0 ? rest[..underscore] : rest;
                if (int.TryParse(numSpan, out var parsed) && parsed > maxIdx)
                {
                    maxIdx = parsed;
                }
            }
        }

        return Math.Max(maxIdx, distinctTurns.Count);
    }

    private string BuildDefaultSystemPromptCore(AgentSessionDto session)
    {
        var reportMd = _reportEngine.RenderMarkdown(_report, withMarkers: true);
        var role = session.IsLead
            ? "Lead Session (Primary Track Representative)"
            : session.BoundAttemptId is not null
                ? $"Contract Subagent (BoundAttemptId={session.BoundAttemptId})"
                : "Collaborative Subagent (Unbound)";

        return $"You are operating in Perga Track '{_track.Title}' ({TrackId}) as {role}.\n\n## Current Report\n{reportMd}";
    }

    private string BuildEpochSeedSystemPrompt(
        AgentSessionDto compactedSession,
        string latestReportMarkdown,
        string handoverSummary)
    {
        var role = compactedSession.IsLead
            ? "Lead Session"
            : compactedSession.BoundAttemptId is not null
                ? $"Contract Subagent (BoundAttemptId={compactedSession.BoundAttemptId})"
                : "Collaborative Subagent";

        return $"""
                You are resuming Perga Track '{_track.Title}' ({TrackId}) as {role} in Epoch {compactedSession.Epoch}.

                ## Latest Track Report
                {latestReportMarkdown}

                ## Epoch {compactedSession.Epoch - 1} Handover Summary
                {handoverSummary}
                """;
    }

    private static string BuildContractSubagentPrompt(TaskDto task)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Execute contract task '{task.TaskKey}' (generation {task.Generation}, access={FormatAccessModeForPolicy(task.Access)}).");
        sb.AppendLine();
        sb.AppendLine("## Goal");
        sb.AppendLine(task.GoalOrCmd);

        if (!string.IsNullOrWhiteSpace(task.Acceptance))
        {
            sb.AppendLine();
            sb.AppendLine("## Acceptance Criteria");
            sb.AppendLine(task.Acceptance);
        }

        if (!string.IsNullOrWhiteSpace(task.SemanticNote))
        {
            sb.AppendLine();
            sb.AppendLine("## Previous Review Note");
            sb.AppendLine(task.SemanticNote);
        }

        sb.AppendLine();
        sb.AppendLine("When finished, call `perga_task_done` with your structured result, or `perga_task_fail` if blocked.");
        return sb.ToString();
    }

    private static string FormatAccessModeForPolicy(AccessMode mode) =>
        mode == AccessMode.ReadOnly ? "read_only" : "read_write";

    private void TrackBackgroundJob(Func<Task> jobFactory)
    {
        var jobId = Guid.NewGuid();
        var task = Task.Run(async () =>
        {
            try
            {
                await jobFactory().ConfigureAwait(false);
            }
            finally
            {
                _backgroundJobs.TryRemove(jobId, out _);
                TouchActivity();
            }
        });
        _backgroundJobs[jobId] = task;
    }

    private long NowMs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private void TouchActivity()
    {
        Volatile.Write(ref _lastActivityAtMs, NowMs());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mailbox.Writer.TryComplete();
        _actorShutdownCts.Cancel();

        try
        {
            await _mailboxLoopTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown cancellation.
        }

        foreach (var kvp in _openAgentSessions.ToArray())
        {
            _openAgentSessions.Remove(kvp.Key);
            try
            {
                await kvp.Value.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort session disposal on eviction/shutdown.
            }
        }

        _actorShutdownCts.Dispose();
        _ = _crossTrackWakeCallback;
    }

    private sealed class ActiveTurnState(
        string sessionId,
        SessionTurnDto turnDto,
        IAgentSession agentSession,
        CancellationTokenSource turnCts)
    {
        public string SessionId { get; } = sessionId;
        public SessionTurnDto TurnDto { get; } = turnDto;
        public IAgentSession AgentSession { get; } = agentSession;
        public CancellationTokenSource TurnCts { get; } = turnCts;
        public HashSet<string> OwnedAskIds { get; } = new(StringComparer.Ordinal);
        public bool Interrupted { get; set; }
    }

    private sealed class ActiveAttemptState(
        string attemptId,
        string taskId,
        string? sessionId,
        IWorkspaceDriver driver,
        AttemptWorkspaceLease lease,
        TaskAttemptDto attemptDto,
        CancellationTokenSource cts)
    {
        public string AttemptId { get; } = attemptId;
        public string TaskId { get; } = taskId;
        public string? SessionId { get; } = sessionId;
        public IWorkspaceDriver Driver { get; } = driver;
        public AttemptWorkspaceLease Lease { get; } = lease;
        public TaskAttemptDto AttemptDto { get; set; } = attemptDto;
        public CancellationTokenSource Cts { get; } = cts;
    }

    private sealed record PendingAskSuspension(
        AskDto Ask,
        IReadOnlyList<string> Options,
        Task<JsonElement> WaiterTask,
        bool ShouldWakeLead,
        string? LeadWakePrompt);
}
