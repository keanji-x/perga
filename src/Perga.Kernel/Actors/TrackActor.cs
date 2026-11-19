using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor : IAsyncDisposable
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
    private string _policyGeneration = Guid.NewGuid().ToString("N");
    private string? _templateSnapshotSource;
    private MountedWorkspace? _mountedWorkspace;
    private readonly List<TaskDto> _tasks = [];
    private readonly Dictionary<string, AgentSessionDto> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AskDto> _asks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalDto> _terminals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextTurnIndexBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IAgentSession> _openAgentSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Model, string? ReasoningEffort)> _boundAgentModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _openAgentSessionPermissionModes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _reservedWakeSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveTurnState> _activeTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<PendingWake>> _pendingWakes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingAskWaiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveAttemptState> _activeAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionIdsByIdempotencyKey = new(StringComparer.Ordinal);
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

    private int _queuedMailWakes;
    private int _activeAttemptCount;

    public bool HasActiveWork =>
        Volatile.Read(ref _queuedMailWakes) > 0 ||
        Volatile.Read(ref _activeAttemptCount) > 0 ||
        Volatile.Read(ref _sessionResumeHoldCount) > 0 ||
        Volatile.Read(ref _queuedMailboxOps) > 0 ||
        Volatile.Read(ref _inFlightExternalOps) > 0 ||
        !_backgroundJobs.IsEmpty;

    public bool IsSessionOrTaskRunning =>
        _activeTurns.Count > 0 || _activeAttempts.Count > 0 || !_dispatchingInputs.IsEmpty;

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
            await RefreshForeignWorkspacePreparationCoreAsync(ct).ConfigureAwait(false);
            StartWorkspacePreparationCore(retry: false);
            return BuildSnapshotCore();
        }, ct);
    }

    public async Task<MountedWorkspace> EnsureMountedAsync(CancellationToken ct = default)
    {
        await WaitForWorkspacePreparationAsync(ct).ConfigureAwait(false);
        return await EnqueueAsync(_ => EnsureWorkspaceMountedCoreAsync(ct), ct).ConfigureAwait(false);
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
                var reopening = _track.ClosedAtMs is not null && nextTrack.ClosedAtMs is null;
                ct.ThrowIfCancellationRequested();
                nextTrack = nextTrack with { UpdatedAtMs = now };
                _track = nextTrack;
                await EmitTrackUpdatedCoreAsync(now, reopening ? _actorShutdownCts.Token : ct).ConfigureAwait(false);
            }

            if (req.Closed is false)
            {
                StartWorkspacePreparationCore(retry: false);
                await ResumeWorkspaceWorkCoreAsync(_actorShutdownCts.Token).ConfigureAwait(false);
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

    public Task<TrackDto> SetTemplateBySessionAsync(string callerSessionId, string? templateId,
        string? source, string expectedPolicyGeneration, JsonElement? inputs, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, KernelMcpToolNames.TrackSet);
            if (_policyGeneration != expectedPolicyGeneration)
                throw KernelException.PolicyDenied("Active policy changed after template application was authorized.");
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot set a template on a closed track.");
            if ((templateId is null) == (source is null) ||
                templateId is not null && string.IsNullOrWhiteSpace(templateId) ||
                source is not null && string.IsNullOrWhiteSpace(source))
                throw KernelException.InvalidArgument("Specify exactly one non-empty 'templateId' or 'source'.");
            if (inputs is { ValueKind: not JsonValueKind.Object })
                throw KernelException.InvalidArgument("'inputs' must be an object.");

            var nextInputs = JsonHelpers.EnsureClonedObject(inputs);
            CompiledPolicy policy;
            JsonElement state;
            try
            {
                policy = templateId is not null
                    ? _templateCatalog.GetRequiredPolicy(templateId)
                    : _templateCatalog.GetOrCompileSnapshot(source!);
                var init = _policyVm.InitState(policy, nextInputs);
                state = (init.Output.ValueKind != JsonValueKind.Undefined ? init.Output : init.NextState).Clone();
            }
            catch (KernelException) { throw; }
            catch (Exception ex)
            {
                throw KernelException.InvalidArgument($"Template compilation or initialization failed: {ex.Message}");
            }

            var now = NowMs();
            var nextTrack = _track with
            {
                TemplateId = policy.TemplateId,
                TemplateInputs = nextInputs,
                TemplateState = state,
                EnabledPlugins = _track.EnabledPlugins.Concat(policy.Manifest.DefaultPlugins).Distinct(StringComparer.Ordinal).ToArray(),
                UpdatedAtMs = now
            };
            var evt = new EventEnvelopeDto(null, nextTrack.AreaId, TrackId, KernelEventKinds.TrackUpdated,
                JsonHelpers.ToElement(nextTrack), now);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.UpdateTrackTemplateAndEmitAsync(nextTrack, policy.JsSource, evt, ct).ConfigureAwait(false);
            _policy = policy;
            _policyGeneration = Guid.NewGuid().ToString("N");
            _templateSnapshotSource = policy.JsSource;
            var pluginsChanged = !_track.EnabledPlugins.SequenceEqual(nextTrack.EnabledPlugins, StringComparer.Ordinal);
            _track = nextTrack;
            if (pluginsChanged)
                _mcpBridge.UpdateTrackPlugins(TrackId, nextTrack.EnabledPlugins);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
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
        var reserved = false;
        try
        {
            await DeleteTrackCoreAsync(reserve: () => reserved = true, committed: () => reserved = false, ct).ConfigureAwait(false);
        }
        catch
        {
            if (reserved)
                await EnqueueAsync(_ =>
                {
                    _deletingTrack = false;
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DeleteTrackCoreAsync(Action reserve, Action committed, CancellationToken ct)
    {
        (WorkspaceSpec? Spec, IWorkspaceDriver Driver, string? CloseCmd, string Cwd, string AreaId) teardownInfo = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);

            await RefreshForeignWorkspacePreparationCoreAsync(ct).ConfigureAwait(false);
            if (_workspacePreparationJob is { IsCompleted: false } ||
                _workspacePreparation is { Phase: WorkspacePreparationPhase.Provisioning or WorkspacePreparationPhase.Mounting or WorkspacePreparationPhase.Initializing })
                throw KernelException.Conflict("Wait for workspace setup to finish before deleting this Track.");
            if (_workspacePreparation is { Phase: WorkspacePreparationPhase.Unknown, Spec: null })
                throw KernelException.PreconditionFailed("Confirm the previous workspace allocation before deleting this Track.");

            if (_deletingTrack)
                throw KernelException.Conflict("This Track is already being deleted.");
            RequireAttemptsOutsideVerification(_activeAttempts.Values);
            if (_sessionResumeHolds.Values.Any(hold => hold.Busy))
                throw KernelException.Conflict("Wait for provider recovery before deleting this Track.");
            ct.ThrowIfCancellationRequested();
            _deletingTrack = true;
            reserve();

            // 1. Cancel active turns
            foreach (var turn in _activeTurns.Values.ToArray())
            {
                turn.Interrupted = true;
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
                    _terminals.Remove(term.Id);
                }
                catch
                {
                    // Keep failed kills available for a retry if teardown cannot unmount.
                }
            }

            // 5. Dispose open agent sessions & revoke MCP tokens
            foreach (var kvp in _openAgentSessions.ToArray())
            {
                _openAgentSessions.Remove(kvp.Key);
                _boundAgentModels.Remove(kvp.Key);
                _openAgentSessionPermissionModes.Remove(kvp.Key);
                await kvp.Value.DisposeAsync().ConfigureAwait(false);
            }

            _mcpBridge.RevokeTrack(TrackId);

            var driver = _workspaceDrivers.GetRequired(_track.WorkspaceDriver);
            var spec = WorkspaceReady ? ExtractWorkspaceSpec(_track) : _workspacePreparation?.Spec;
            var closeCmd = WorkspaceReady ? _policy?.Manifest.WorkspaceCloseCmd : null;
            var cwd = _mountedWorkspace?.MergedCwd ?? _track.Cwd;
            _mountedWorkspace = null;
            return (spec, driver, closeCmd, cwd, _track.AreaId);
        }, CancellationToken.None).ConfigureAwait(false);

        // Run WorkspaceCloseCmd before tearing down workspace
        if (!string.IsNullOrWhiteSpace(teardownInfo.CloseCmd))
        {
            try
            {
                var cwd = teardownInfo.Spec is null
                    ? teardownInfo.Cwd
                    : (await teardownInfo.Driver.EnsureMountedAsync(teardownInfo.Spec, ct).ConfigureAwait(false)).MergedCwd;
                await _commandRunner.RunAsync(
                    new ShellCommandRequest(
                        teardownInfo.CloseCmd,
                        cwd,
                        TimeSpan.FromSeconds(_options.DefaultHookTimeoutSecs)),
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close command before physical teardown.
            }
        }

        if (teardownInfo.Spec is not null)
        {
            try
            {
                await teardownInfo.Driver.TeardownTrackAsync(teardownInfo.Spec, ct).ConfigureAwait(false);
            }
            catch (WorkspaceUnavailableException ex)
            {
                throw KernelException.PreconditionFailed(ex.Message);
            }
        }
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.DeleteTrackAsync(TrackId, ct).ConfigureAwait(false);
        committed();

        if (teardownInfo.Spec is not null)
        {
            try
            {
                await teardownInfo.Driver.CleanupDeletedTrackAsync(teardownInfo.Spec).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Could not clean up the deleted workspace for Track {0}: {1}", TrackId, ex.Message);
            }
        }

        var delNow = NowMs();
        var delEvt = new EventEnvelopeDto(
            EventId: null,
            AreaId: teardownInfo.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.TrackDeleted,
            Payload: JsonHelpers.ToElement(new { id = TrackId, trackId = TrackId, areaId = teardownInfo.AreaId, deleted = true }),
            CreatedAtMs: delNow);
        await _store.AppendEventAsync(delEvt, CancellationToken.None).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, delEvt, CancellationToken.None, allowFallback: false).ConfigureAwait(false);
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
        CancellationToken ct,
        WorkerDispatchIntent? dispatch = null)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var phase1 = await EnqueueAsync<(ReportReadResult? Completed, PendingAskSuspension? Ask, ReportCommitRequest EffectiveRequest)>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);

                if (dispatch is not null && await TryReplayWorkerDispatchCoreAsync(dispatch, callerSessionId, ct).ConfigureAwait(false) is { } replay)
                    return (replay, null, null!);

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

                    var suspension = await ApplyGuardDecisionCoreAsync(eval.Output, callerSessionId, ct).ConfigureAwait(false);
                    if (suspension is not null)
                        return (null, suspension, effectiveReq);
                }

                var result = await FinalizeReportCommitCoreAsync(
                    preview,
                    callerSessionId,
                    effectiveReq.CommitMessage ?? $"Report commit ({editorRole})",
                    ct, dispatch).ConfigureAwait(false);
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
                if (dispatch is not null && await TryReplayWorkerDispatchCoreAsync(dispatch, callerSessionId, ct).ConfigureAwait(false) is { } replay)
                    return replay;
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
                    ct, dispatch).ConfigureAwait(false);
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
            var idemKey = string.IsNullOrWhiteSpace(req.IdempotencyKey) ? null : req.IdempotencyKey!.Trim();
            if (idemKey is not null &&
                _sessionIdsByIdempotencyKey.TryGetValue(idemKey, out var existingId) &&
                _sessions.TryGetValue(existingId, out var existingSession))
            {
                return existingSession;
            }

            var session = await NewSubagentSessionCoreAsync(req, ct).ConfigureAwait(false);
            await PersistAndEmitSessionUpdatedCoreAsync(session, session.UpdatedAtMs, ct).ConfigureAwait(false);
            _sessions[session.Id] = session;
            if (idemKey is not null)
            {
                _sessionIdsByIdempotencyKey[idemKey] = session.Id;
            }

            return session;
        }, ct);
    }

    private async Task<string?> ResolveSessionPermissionModeCoreAsync(
        string provider, string? requested, bool isLead, CancellationToken ct)
    {
        var cwd = string.IsNullOrWhiteSpace(_track.Cwd) ? _workspacePreparation?.Request.SourcePath : _track.Cwd;
        IReadOnlyList<OverlayMountSpec>? overlays = null;
        if (provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(requested) && !string.IsNullOrWhiteSpace(_track.Cwd))
        {
            var mounted = await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
            cwd = mounted.MergedCwd;
            overlays = MapOverlayLayers(mounted.ProcessOverlayLayers);
        }
        return await RuntimePermissionModes.ResolveAsync(_agentRuntime, provider, requested, ct,
            isLead ? _options.LeadSandboxMode : _options.FreeSubagentSandboxMode, overlays, cwd).ConfigureAwait(false);
    }

    private async Task<AgentSessionDto> NewSubagentSessionCoreAsync(CreateSessionRequest req, CancellationToken ct)
    {
        var now = NowMs();
        var provider = string.IsNullOrWhiteSpace(req.Provider)
            ? GetDefaultProviderCore()
            : (TaskBlockSynchronizer.NormalizeProviderId(req.Provider) ?? req.Provider!.Trim());
        var permissionMode = await ResolveSessionPermissionModeCoreAsync(provider, req.PermissionMode, isLead: false, ct).ConfigureAwait(false);

        return new AgentSessionDto(
            Id: $"ses_{Guid.NewGuid():N}",
            TrackId: TrackId,
            IsLead: false,
            BoundTaskId: null,
            BoundAttemptId: null,
            Title: string.IsNullOrWhiteSpace(req.Title) ? "Subagent" : req.Title,
            Provider: provider,
            Model: req.Model ?? GetDefaultModelCore(provider),
            NativeThreadId: null,
            Epoch: 1,
            Status: "idle",
            ContextUsageRatio: 0.0,
            ArchivedAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            ReasoningEffort: string.IsNullOrWhiteSpace(req.ReasoningEffort) ? null : req.ReasoningEffort.Trim(),
            PermissionMode: permissionMode);
    }

    public Task<AgentSessionDto> PatchSessionAsync(string sessionId, PatchSessionRequest req, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var existing = RequireSessionCore(sessionId);

            var nextProvider = string.IsNullOrWhiteSpace(req.Provider)
                ? existing.Provider
                : (TaskBlockSynchronizer.NormalizeProviderId(req.Provider) ?? req.Provider!.Trim());

            string? nextModel;
            if (req.ClearModel)
            {
                nextModel = null;
            }
            else if (req.Model is not null)
            {
                nextModel = string.IsNullOrWhiteSpace(req.Model) ? null : req.Model.Trim();
            }
            else
            {
                nextModel = existing.Model;
            }

            if (existing.BoundAttemptId is not null && (req.Provider is not null || req.Model is not null || req.ClearModel))
                throw KernelException.InvalidArgument("Worker configuration uses the attempt settings endpoint.");

            var providerChanged = !string.Equals(nextProvider, existing.Provider, StringComparison.Ordinal);
            var modelChanged = !string.Equals(nextModel, existing.Model, StringComparison.Ordinal);

            if (providerChanged || modelChanged || req.Archived == true)
                RequireSessionDispatchAvailableCore(sessionId);
            if ((providerChanged || modelChanged) &&
                (_activeTurns.ContainsKey(sessionId) || HoldsDurableSlot(existing) || _compactingSessions.Contains(sessionId)))
            {
                throw KernelException.Conflict(
                    $"Cannot change provider or model on session '{sessionId}' while a turn is actively running.");
            }

            if (providerChanged || modelChanged)
            {
                _mcpBridge.RevokeSession(sessionId);
                if (_openAgentSessions.Remove(sessionId, out var cachedSession))
                {
                    await cachedSession.DisposeAsync().ConfigureAwait(false);
                }
            }

            var now = NowMs();
            var nextTitle = req.Title is not null ? req.Title.Trim() : existing.Title;
            var nextArchived = req.Archived.HasValue
                ? (req.Archived.Value ? (existing.ArchivedAtMs ?? now) : null)
                : existing.ArchivedAtMs;
            var nextNativeThreadId = providerChanged ? null : existing.NativeThreadId;
            var nextPermissionMode = providerChanged
                ? await ResolveSessionPermissionModeCoreAsync(nextProvider, null, existing.IsLead, ct).ConfigureAwait(false)
                : existing.PermissionMode;

            var updated = existing with
            {
                Title = nextTitle,
                Provider = nextProvider,
                Model = nextModel,
                NativeThreadId = nextNativeThreadId,
                PermissionMode = nextPermissionMode,
                ArchivedAtMs = nextArchived,
                UpdatedAtMs = now
            };

            await PersistAndEmitSessionUpdatedCoreAsync(updated, now, ct).ConfigureAwait(false);
            _sessions[sessionId] = updated;
            return updated;
        }, ct);
    }

    public Task<SessionItemsPageDto> GetSessionItemsAsync(string sessionId, long afterId = 0, int limit = 100, CancellationToken ct = default,
        bool latest = false, long? beforeId = null, string? callerSessionId = null, string? activityTurnId = null, long? afterSequence = null)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireWorkerOperatorCore(callerSessionId);
            var session = RequireSessionCore(sessionId);
            if (callerSessionId is not null && session.BoundAttemptId is null)
                throw KernelException.Forbidden("This tool reads Worker sessions on the current Track.");
            var (items, hasMoreBefore) = await _store
                .ListSessionItemsPageAsync(sessionId, afterId, limit, ct, latest, beforeId)
                .ConfigureAwait(false);
            var activeTurn = _activeTurns.TryGetValue(sessionId, out var running) ? running.TurnDto : null;
            var unknown = session.Status == "unknown"
                ? await _store.SessionInputs.FindUnresolvedAsync(sessionId, ct).ConfigureAwait(false)
                : null;
            var worker = await ReadWorkerActivityCoreAsync(session, running, activityTurnId, afterSequence, ct).ConfigureAwait(false);
            var queue = await ReadMessageQueueCoreAsync(sessionId, ct).ConfigureAwait(false);
            return new SessionItemsPageDto(items.Select(item => WithMessageStatus(item, running)).ToArray(),
                activeTurn, session, unknown?.Receipt, hasMoreBefore,
                queue.Queue, running?.CanSteerQueuedMessages == true, queue.Handoffs,
                ReadSessionResumeCore(session, running, unknown?.Receipt), worker);
        }, ct);
    }

    public async Task<SessionTurnDto> SendMessageAsync(string sessionId, SendMessageRequest req, CancellationToken ct = default, bool requireActiveTurn = false)
    {
        if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            throw KernelException.InvalidArgument("IdempotencyKey is required.");
        }

        var dispatch = await EnqueueAsync<(Task<SessionTurnDto>? RunningOrCachedTask, ActiveTurnState? SteerTarget)>(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);

            var dedupKey = (sessionId, req.IdempotencyKey);
            if (_idempotencyTurns.TryGetValue(dedupKey, out var existing))
            {
                return (existing, null);
            }

            RequireSessionDispatchAvailableCore(sessionId);
            if (_activeTurns.TryGetValue(sessionId, out var active))
            {
                if (!req.SteerIfRunning)
                {
                    throw KernelException.Conflict($"Session '{sessionId}' already has an active turn in progress.");
                }

                return (null, active);
            }

            if (requireActiveTurn || HoldsDurableSlot(session) || _compactingSessions.Contains(sessionId))
                throw KernelException.Conflict($"Session '{sessionId}' cannot accept this send.");

            var turnTcs = new TaskCompletionSource<SessionTurnDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            _idempotencyTurns[dedupKey] = turnTcs.Task;

            _ = actor.TrackBackgroundJob(async () =>
            {
                try
                {
                    var completed = await ExecuteSessionTurnPipelineAsync(
                            sessionId,
                            "user",
                            req.Content,
                            _actorShutdownCts.Token)
                        .ConfigureAwait(false);
                    turnTcs.TrySetResult(completed);
                }
                catch (Exception ex)
                {
                    turnTcs.TrySetException(ex);
                    await EnqueueAsync(_ =>
                    {
                        _idempotencyTurns.Remove(dedupKey);
                        return Task.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            });

            return (turnTcs.Task, null);
        }, ct).ConfigureAwait(false);

        if (dispatch.SteerTarget is not null)
        {
            await SteerMessageAsync(sessionId, dispatch.SteerTarget.TurnDto.Id, req.Content, ct).ConfigureAwait(false);

            await EnqueueAsync(_ =>
            {
                _idempotencyTurns[(sessionId, req.IdempotencyKey)] = Task.FromResult(dispatch.SteerTarget.TurnDto);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);

            return dispatch.SteerTarget.TurnDto;
        }

        return await dispatch.RunningOrCachedTask!.WaitAsync(ct).ConfigureAwait(false);
    }

    public Task<AgentSessionDto> UpdateSessionModelAsync(
        string sessionId, UpdateSessionModelRequest req, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            if (session.BoundAttemptId is not null)
                throw KernelException.InvalidArgument("Worker models must be changed through Worker settings.");
            var model = string.IsNullOrWhiteSpace(req.Model) ? null : req.Model.Trim();
            var reasoningEffort = string.IsNullOrWhiteSpace(req.ReasoningEffort)
                ? null
                : req.ReasoningEffort.Trim();
            var updated = session with { Model = model, ReasoningEffort = reasoningEffort, UpdatedAtMs = NowMs() };
            await PersistAndEmitSessionUpdatedCoreAsync(updated, updated.UpdatedAtMs, ct).ConfigureAwait(false);

            // Never change the provider underneath an active turn. Its next reuse reconciles.
            if (!_activeTurns.ContainsKey(sessionId) && !_compactingSessions.Contains(sessionId) &&
                !_sessionResumeHolds.ContainsKey(sessionId) && _openAgentSessions.ContainsKey(sessionId))
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _actorShutdownCts.Token);
                await GetOrOpenAgentSessionCoreAsync(updated, overrideSystemPrompt: null, linkedCts.Token).ConfigureAwait(false);
            }

            return updated;
        }, ct);
    }

    public Task InterruptSessionAsync(string sessionId, CancellationToken ct = default) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            if (session.BoundAttemptId is not null)
                throw KernelException.InvalidArgument("Worker interruption requires attemptId and turnId.");
            if (_sessionResumeHolds.TryGetValue(sessionId, out var recovery) && recovery.Busy)
                throw KernelException.Conflict("Provider recovery is already in progress.");
            if (!_activeTurns.TryGetValue(sessionId, out var active))
            {
                if (session.Status == "queued")
                    throw KernelException.Conflict("This message has been accepted and is waiting to start.");
                return;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _actorShutdownCts.Token);
            deadline.CancelAfter(ProviderStopGrace);
            active.Interrupted = true;
            try
            {
                await active.AgentSession.CancelTurnAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_actorShutdownCts.IsCancellationRequested)
            {
                throw KernelException.PreconditionFailed("The provider did not answer the stop request.");
            }

            if (!active.AgentSession.RequiresTerminalStop && !_sessionResumeHolds.ContainsKey(sessionId))
                active.TurnCts.Cancel();
        }, ct);

    public async Task<AgentSessionDto> CompactSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<AgentSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = TrackBackgroundJob(async () =>
        {
            try
            {
                var compacted = await CompactSessionInternalAsync(sessionId, _actorShutdownCts.Token).ConfigureAwait(false);
                tcs.TrySetResult(compacted);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<AgentSessionDto> SetSessionPermissionModeAsync(string sessionId, string permissionMode, CancellationToken ct = default)
    {
        IAgentSession? toDispose = null;
        var updated = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            if (session.BoundTaskId is not null)
            {
                throw KernelException.InvalidArgument("Cannot change permission mode on a task-bound session.");
            }

            if (string.IsNullOrWhiteSpace(permissionMode))
                throw KernelException.InvalidArgument("Permission mode is required.");
            var trimmed = await ResolveSessionPermissionModeCoreAsync(session.Provider, permissionMode, session.IsLead, ct).ConfigureAwait(false);
            if (string.Equals(session.PermissionMode, trimmed, StringComparison.Ordinal))
            {
                return session;
            }

            var next = session with
            {
                PermissionMode = trimmed,
                UpdatedAtMs = NowMs()
            };
            await PersistAndEmitSessionUpdatedCoreAsync(next, next.UpdatedAtMs, ct).ConfigureAwait(false);
            _sessions[sessionId] = next;

            // If idle (neither running a turn nor compacting), close any cached provider subprocess so the next turn launches with the new mode.
            if (!_activeTurns.ContainsKey(sessionId) &&
                !_compactingSessions.Contains(sessionId) &&
                _openAgentSessions.Remove(sessionId, out var open))
            {
                _openAgentSessionPermissionModes.Remove(sessionId);
                _boundAgentModels.Remove(sessionId);
                toDispose = open;
            }

            return next;
        }, ct).ConfigureAwait(false);

        if (toDispose is not null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }

        return updated;
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

    public Task<TaskDto> CancelTaskAsync(string? callerSessionId, string taskKeyOrId, string? reason = null, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (callerSessionId is not null)
            {
                RequireGovernanceSessionCore(callerSessionId, "perga_task_cancel");
            }

            var task = RequireTaskCore(taskKeyOrId);

            var now = NowMs();
            TaskAttemptDto? settledAttempt = null;
            JsonElement? nextTemplateState = null;

            if (task.LatestAttemptId is not null &&
                _activeAttempts.TryGetValue(task.LatestAttemptId, out var activeAttempt))
            {
                settledAttempt = await CancelAndDiscardActiveAttemptCoreAsync(activeAttempt, reason ?? "Canceled by governance.", now, ct)
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
                StatusDetail = string.IsNullOrWhiteSpace(reason) ? "Canceled." : reason!.Trim(),
                UpdatedAtMs = now
            };
            ReplaceTaskInMemoryCore(updatedTask);

            await PersistAndEmitTaskUpdatedCoreAsync(updatedTask, settledAttempt, nextTemplateState, now, ct).ConfigureAwait(false);
            await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
            return updatedTask;
        }, ct);
    }

    public Task<TaskDto> AcceptTaskAsync(string? callerSessionId, string taskKeyOrId, string? note = null, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (callerSessionId is not null)
            {
                RequireGovernanceSessionCore(callerSessionId, "perga_task_accept");
            }

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
        string? callerSessionId,
        string taskKeyOrId,
        string note,
        bool reQueue = false,
        CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (callerSessionId is not null)
            {
                RequireGovernanceSessionCore(callerSessionId, "perga_task_reject");
            }

            var task = RequireTaskCore(taskKeyOrId);
            if (task.LatestAttemptId is { } latestAttempt && _activeAttempts.ContainsKey(latestAttempt))
                throw KernelException.Conflict("Wait for the current attempt to finish before rejecting or requeuing the task.");
            var effectiveNote = string.IsNullOrWhiteSpace(note) ? "Rejected by user." : note.Trim();

            var now = NowMs();
            var nextStatus = reQueue && task.Ready ? TaskRunStatus.Pending : TaskRunStatus.Failed;
            var updated = task with
            {
                SemanticVerdict = reQueue ? SemanticVerdict.None : SemanticVerdict.Rejected,
                SemanticNote = effectiveNote,
                Status = nextStatus,
                StatusDetail = $"Rejected: {effectiveNote}",
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
        CancellationToken ct = default, WorkerCompletionOrigin? origin = null)
    {
        var attemptId = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var caller = RequireBoundAttemptSessionCore(callerSessionId, "perga_task_done");
            return caller.BoundAttemptId!;
        }, ct).ConfigureAwait(false);

        return await CompleteAttemptDoneByAttemptIdAsync(attemptId, workerResult, ct, origin).ConfigureAwait(false);
    }

    public async Task<TaskDto> CompleteAttemptFailAsync(
        string callerSessionId,
        string reason,
        JsonElement? workerResult = null,
        CancellationToken ct = default, WorkerCompletionOrigin? origin = null)
    {
        var attemptId = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var caller = RequireBoundAttemptSessionCore(callerSessionId, "perga_task_fail");
            return caller.BoundAttemptId!;
        }, ct).ConfigureAwait(false);

        return await CompleteAttemptFailByAttemptIdAsync(attemptId, reason, workerResult, ct, origin).ConfigureAwait(false);
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
        var outcome = await EnqueueAsync<(AskDto AnsweredAsk, bool ShouldWakeLead)>(async _ =>
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

            if (existingAsk.Delivery == AskDelivery.Hold &&
                (_track.ClosedAtMs is not null ||
                 !_pendingAskWaiters.TryGetValue(askId, out var liveWaiter) || liveWaiter.Task.IsCompleted ||
                 existingAsk.SessionId is null ||
                 (existingAsk.TurnId is not null &&
                  (!_activeTurns.TryGetValue(existingAsk.SessionId, out var owner) || owner.TurnDto.Id != existingAsk.TurnId ||
                   owner.Interrupted || owner.TurnCts.IsCancellationRequested))))
                throw KernelException.Conflict("This approval request is no longer pending. Check the current turn before trying again.");

            var now = NowMs();
            var clonedAnswer = answer.Clone();
            var answered = existingAsk with
            {
                Status = AskStatus.Answered,
                Answer = clonedAnswer,
                AnsweredAtMs = now
            };

            var evt = new EventEnvelopeDto(
                EventId: null,
                AreaId: _track.AreaId,
                TrackId: TrackId,
                Kind: KernelEventKinds.AskChanged,
                Payload: JsonHelpers.ToElement(answered),
                CreatedAtMs: now);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.UpsertAskAsync(answered, evt, ct).ConfigureAwait(false);

            // The transaction accepted this answer. Neither request cancellation nor
            // event catch-up failure may reopen it or strand its original waiter.
            _asks[askId] = answered;
            _pendingAskWaiters.Remove(askId, out var waiter);
            try
            {
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                // Complete inside the actor: EnqueueAsync's caller can stop waiting
                // on cancellation before this committed operation returns.
                waiter?.TrySetResult(clonedAnswer);
            }

            // Determine whether Lead is ALREADY actively streaming the exact turn that owns this ask.
            var leadOwnsActiveAsk = false;
            if (_track.LeadSessionId is { } leadId &&
                _activeTurns.TryGetValue(leadId, out var leadTurn) &&
                leadTurn.OwnedAskIds.Contains(askId))
            {
                leadOwnsActiveAsk = true;
            }

            var shouldWakeLead = existingAsk.Delivery == AskDelivery.Wake && !leadOwnsActiveAsk && _track.ClosedAtMs is null && !string.IsNullOrEmpty(_track.LeadSessionId);
            return (answered, shouldWakeLead);
        }, ct).ConfigureAwait(false);

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
        string? toTrackId,
        string summary,
        string body,
        Func<MailDto, CancellationToken, Task> deliverToTargetTrack,
        CancellationToken ct, string? replyToMailId)
    {
        var mail = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, "perga_mail_send");
            if ((toTrackId is null) == (replyToMailId is null) ||
                string.IsNullOrWhiteSpace(toTrackId ?? replyToMailId))
                throw KernelException.InvalidArgument("Specify exactly one of toTrackId and replyToMailId.");
            if (replyToMailId is not null)
            {
                var parent = await _store.FindReceivedMailAsync(TrackId, replyToMailId, ct).ConfigureAwait(false)
                    ?? throw KernelException.NotFound("Original mail was not found in this Track's inbox.");
                toTrackId = parent.FromTrackId;
            }

            var now = NowMs();
            return new MailDto(
                Id: $"mail_{Guid.NewGuid():N}",
                FromTrackId: TrackId,
                ToTrackId: toTrackId!,
                Summary: summary,
                Body: body,
                ReadAtMs: null,
                CreatedAtMs: now,
                ReplyToMailId: replyToMailId);
        }, ct).ConfigureAwait(false);

        await deliverToTargetTrack(mail, ct).ConfigureAwait(false);
        return mail;
    }

    internal async Task ReceiveMailAsync(MailDto mail, CancellationToken ct = default)
    {
        var shouldWake = await EnqueueAsync(async _ =>
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
            var requestWake = _track.ClosedAtMs is null && _track.LeadSessionId is not null;
            await _store.UpsertMailAsync(mail, evt, ct, requestWake).ConfigureAwait(false);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
            return requestWake;
        }, ct).ConfigureAwait(false);

        if (shouldWake)
            await WakeForMailAsync(mail, _actorShutdownCts.Token).ConfigureAwait(false);
    }

    internal Task WakeForMailAsync(MailDto mail, CancellationToken ct, bool recovering = false) =>
        DispatchLeadWakePromptIfNeededAsync("wake",
            $"New cross-track mail '{mail.Id}' from track '{mail.FromTrackId}': {mail.Summary}\n\n{mail.Body}",
            ct, mail.Id, recovering);

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
            await _store.UpsertMailAsync(readMail, evt, ct, requestWake: false).ConfigureAwait(false);
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

    public async Task<TerminalDto> OpenTerminalAsync(string? callerSessionId, OpenTerminalRequest req, CancellationToken ct = default)
    {
        await WaitForWorkspacePreparationAsync(ct).ConfigureAwait(false);
        return await EnqueueAsync(async _ =>
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
        }, ct).ConfigureAwait(false);
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
            if (!_terminals.Remove(terminalId, out var existing))
            {
                throw KernelException.NotFound($"Terminal '{terminalId}' not found on track '{TrackId}'.");
            }

            var now = NowMs();
            var closed = existing with
            {
                Status = TerminalStatus.Exited,
                ExitCode = existing.ExitCode ?? 0,
                ExitedAtMs = existing.ExitedAtMs ?? now
            };

            var payload = JsonHelpers.ToElement(new
            {
                id = closed.Id,
                terminalId = closed.Id,
                trackId = closed.TrackId,
                attemptId = closed.AttemptId,
                idempotencyKey = closed.IdempotencyKey,
                title = closed.Title,
                program = closed.Program,
                cwd = closed.Cwd,
                status = "exited",
                exitCode = closed.ExitCode,
                createdAtMs = closed.CreatedAtMs,
                exitedAtMs = closed.ExitedAtMs,
                deleted = true
            });
            var evt = new EventEnvelopeDto(
                EventId: null,
                AreaId: _track.AreaId,
                TrackId: TrackId,
                Kind: KernelEventKinds.TerminalChanged,
                Payload: payload,
                CreatedAtMs: now);

            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.DeleteTerminalAsync(TrackId, terminalId, evt, ct).ConfigureAwait(false);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
            return closed;
        }, ct).ConfigureAwait(false);
    }

    public async Task<string> GuardMcpToolCallAsync(
        string sessionId,
        string mcpServer,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            string authorizedPolicyGeneration = null!;
            var askSuspension = await EnqueueAsync<PendingAskSuspension?>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var caller = RequireSessionCore(sessionId);
                if (string.Equals(mcpServer, "perga", StringComparison.OrdinalIgnoreCase) &&
                    KernelMcpCatalog.IsGovernanceOnly(toolName))
                    RequireGovernanceSessionCore(sessionId, toolName);

                if (!string.Equals(mcpServer, "perga", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mcpServer, "vcs", StringComparison.OrdinalIgnoreCase) &&
                    !_track.EnabledPlugins.Contains(mcpServer, StringComparer.Ordinal))
                {
                    throw KernelException.Forbidden(
                        $"MCP plugin '{mcpServer}' is not enabled on track '{TrackId}'.");
                }

                // Capture before evaluation, including across an AskHuman suspension.
                authorizedPolicyGeneration = _policyGeneration;
                if (mcpServer == "perga" && KernelMcpCatalog.IsWorkerOperatorOnly(toolName))
                    RequireWorkerOperatorCore(caller.Id);

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
                // An allowed replacement initializes its own state atomically. Denied or
                // suspended attempts still retain the current policy's guard state updates.
                if (!string.Equals(mcpServer, "perga", StringComparison.OrdinalIgnoreCase) ||
                    toolName != KernelMcpToolNames.TrackSet || eval.Output is not GuardDecision.Allow)
                    await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);

                return await ApplyGuardDecisionCoreAsync(eval.Output, caller.Id, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (askSuspension is null)
            {
                return authorizedPolicyGeneration;
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
            return authorizedPolicyGeneration;
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
            return;

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

        _policyGeneration = Guid.NewGuid().ToString("N");

        _tasks.Clear();
        _tasks.AddRange(snapshot.Tasks);

        _sessions.Clear();
        foreach (var s in snapshot.Sessions)
        {
            var isLead = string.Equals(s.Id, _track.LeadSessionId, StringComparison.Ordinal);
            var normalized = s.IsLead == isLead ? s : s with { IsLead = isLead };
            _sessions[normalized.Id] = normalized;

            _nextTurnIndexBySession[normalized.Id] = await _store.GetLastTurnIndexAsync(normalized.Id, ct)
                .ConfigureAwait(false);
        }

        RestoreSessionResumeFencesCore(await _store.ListSessionResumeFencesAsync(TrackId, ct).ConfigureAwait(false));

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

        _workspacePreparation = await _store.GetWorkspacePreparationAsync(TrackId, ct).ConfigureAwait(false);
        _hydrated = true;
    }

    private async Task<MountedWorkspace> EnsureWorkspaceMountedCoreAsync(CancellationToken ct)
    {
        RequireWorkspaceReadyCore();
        var driver = _workspaceDrivers.GetRequired(_track.WorkspaceDriver);
        if (_mountedWorkspace is not null && !driver.VerifyMountOnAccess)
            return _mountedWorkspace;

        var spec = ExtractWorkspaceSpec(_track);
        try
        {
            _mountedWorkspace = await driver.EnsureMountedAsync(spec, ct).ConfigureAwait(false);
            return _mountedWorkspace;
        }
        catch (WorkspaceUnavailableException ex)
        {
            throw KernelException.PreconditionFailed(ex.Message);
        }
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
        CancellationToken ct,
        WorkerDispatchIntent? dispatch = null)
    {
        var now = NowMs();
        var requireGate = _policy?.Manifest.RequireGateForReadWrite ?? false;
        var syncOutcome = TaskBlockSynchronizer.SyncTasksFromReport(
            TrackId,
            commitResult.NextDocument,
            _tasks,
            requireGate,
            now);

        RequireAttemptsOutsideVerification(_activeAttempts.Values.Where(attempt =>
            syncOutcome.SupersededOrRemovedActiveTaskIds.Contains(attempt.TaskId)));
        foreach (var supersededTaskId in syncOutcome.SupersededOrRemovedActiveTaskIds)
        {
            var active = _activeAttempts.Values.FirstOrDefault(a =>
                string.Equals(a.TaskId, supersededTaskId, StringComparison.Ordinal));
            if (active is not null)
            {
                const string reason = "Task contract superseded or removed in report.";
                var canceled = await CancelAndDiscardActiveAttemptCoreAsync(active, reason, now, ct).ConfigureAwait(false);
                var superseded = RequireTaskCore(active.TaskId) with { Status = TaskRunStatus.Canceled, StatusDetail = reason, UpdatedAtMs = now };
                await PersistAndEmitTaskUpdatedCoreAsync(superseded, canceled, null, now, ct).ConfigureAwait(false);
                ReplaceTaskInMemoryCore(superseded);
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
            ct, dispatch).ConfigureAwait(false);

        _report = commitResult.NextDocument;
        _tasks.Clear();
        _tasks.AddRange(syncOutcome.SyncedTasks);
        var committedCt = _actorShutdownCts.Token;
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, reportEvt, committedCt).ConfigureAwait(false);

        var taskEvt = CreateTaskUpdatedEventCore(now);
        await _store.AppendEventAsync(taskEvt, committedCt).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, taskEvt, committedCt).ConfigureAwait(false);

        await ScheduleEligibleTasksCoreAsync(committedCt).ConfigureAwait(false);

        var rendered = _reportEngine.RenderMarkdown(_report, withMarkers: true);
        var knownRevs = _report.Blocks.ToDictionary(b => b.Id, b => b.Rev, StringComparer.Ordinal);
        return new ReportReadResult(
            DocRev: _report.DocRev,
            Summary: _report.Summary,
            RenderedMarkdown: rendered,
            Blocks: _report.Blocks,
            Tasks: _tasks.ToArray(),
            Anchor: new ReadAnchor(_report.DocRev, knownRevs, WholeDocumentRead: true),
            Diagnostics: syncOutcome.Diagnostics,
            Changes: commitResult.Changes);
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
        if (_deletingTrack || _track.ClosedAtMs is not null || !WorkspaceReady)
        {
            return;
        }

        var mounted = await EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
        var driver = _workspaceDrivers.GetRequired(_track.WorkspaceDriver);
        bool preparationFailed;
        do
        {
            preparationFailed = false;
            var schedulable = TaskGraphScheduler.ComputeSchedulable(_tasks, driver.SupportsParallelIsolatedWriteLeases);
            foreach (var task in schedulable)
            {
                if (!await StartTaskAttemptCoreAsync(task, driver, mounted, ct).ConfigureAwait(false))
                    preparationFailed = true;
            }
        } while (preparationFailed);
    }

    private async Task<bool> StartTaskAttemptCoreAsync(
        TaskDto task,
        IWorkspaceDriver driver,
        MountedWorkspace mounted,
        CancellationToken ct)
    {
        var now = NowMs();
        var attemptId = $"att_{Guid.NewGuid():N}";
        var isCommandTask = string.Equals(task.Kind, "command", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(task.Kind, "cmd", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(task.Kind, "shell", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(task.Kind, "terminal", StringComparison.OrdinalIgnoreCase);
        var workerProvider = string.IsNullOrWhiteSpace(task.Provider) ? GetDefaultProviderCore() : task.Provider!;
        var subagent = isCommandTask ? null : new AgentSessionDto(
            Id: $"ses_{Guid.NewGuid():N}",
            TrackId: TrackId,
            IsLead: false,
            BoundTaskId: task.Id,
            BoundAttemptId: attemptId,
            Title: $"Task: {task.TaskKey}",
            Provider: workerProvider,
            Model: ResolveTaskWorkerModelCore(task),
            NativeThreadId: null,
            Epoch: 1,
            Status: "idle",
            ContextUsageRatio: 0.0,
            ArchivedAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            PermissionMode: await RuntimePermissionModes.ResolveAsync(_agentRuntime, workerProvider, null, ct, task.Access == AccessMode.ReadOnly ? _options.ReadOnlyTaskSandboxMode : _options.ReadWriteTaskSandboxMode, null).ConfigureAwait(false));

        var runningTask = task with { Status = TaskRunStatus.Running, StatusDetail = "Allocating workspace.",
            LatestAttemptId = attemptId, UpdatedAtMs = now };
        var reservation = new TaskAttemptDto(attemptId, task.Id, TrackId, null, "running", null,
            JsonHelpers.EmptyObject, null, null, now, null);

        await _store.UpsertTaskAndAttemptAsync(runningTask, reservation, null, null, ct).ConfigureAwait(false);
        ReplaceTaskInMemoryCore(runningTask);
        AttemptWorkspaceLease? lease = null;
        TaskAttemptDto attempt;
        var sessionEvent = subagent is null ? null : SessionUpdatedEvent(subagent);
        try
        {
            lease = await driver.AcquireAttemptLeaseAsync(mounted, attemptId, task.Access == AccessMode.ReadWrite, ct)
                .ConfigureAwait(false);
            attempt = reservation with { SessionId = subagent?.Id, WorkspaceLease = JsonHelpers.ToElement(lease) };
            runningTask = runningTask with { StatusDetail = null };
            var taskEvent = CreateTaskUpdatedEventCore(now);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.UpsertTaskAndAttemptAsync(runningTask, attempt, null, taskEvent, ct, subagent, sessionEvent).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (lease is not null)
                await driver.SettleAttemptLeaseAsync(lease, AttemptLeaseDisposition.Discard, CancellationToken.None).ConfigureAwait(false);
            var failed = runningTask with { Status = TaskRunStatus.Failed, StatusDetail = ex.Message, UpdatedAtMs = NowMs() };
            await PersistAndEmitTaskUpdatedCoreAsync(failed, reservation with { Status = "failed", FinishedAtMs = failed.UpdatedAtMs },
                null, failed.UpdatedAtMs, CancellationToken.None).ConfigureAwait(false);
            ReplaceTaskInMemoryCore(failed);
            return false;
        }

        ReplaceTaskInMemoryCore(runningTask);
        if (subagent is not null)
        {
            _sessions[subagent.Id] = subagent;
            _nextTurnIndexBySession[subagent.Id] = 0;
        }

        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        _activeAttempts[attemptId] = new ActiveAttemptState(attemptId, task.Id, subagent?.Id, driver, lease, attempt, attemptCts);
        Volatile.Write(ref _activeAttemptCount, _activeAttempts.Count);
        var attemptToken = attemptCts.Token;
        if (subagent is null)
            _ = TrackBackgroundJob(() => RunCommandTaskAttemptInBackgroundAsync(attemptId, runningTask, lease, attemptToken));
        else
        {
            var prompt = BuildContractSubagentPrompt(runningTask);
            var startupReservation = Guid.NewGuid();
            _reservedWakeSessions.Add(subagent.Id, startupReservation);
            _ = TrackBackgroundJob(() => RunSubagentTaskAttemptInBackgroundAsync(attemptId, subagent.Id, prompt, startupReservation, attemptToken));
        }

        var evt = sessionEvent ?? CreateTaskUpdatedEventCore(now);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private const int TaskOutputTailChars = 4096;

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
                new ShellCommandRequest(task.GoalOrCmd, lease.EffectiveCwd, TimeSpan.FromSeconds(Math.Max(1, timeoutSecs)),
                    OutputTailChars: TaskOutputTailChars),
                ct).ConfigureAwait(false);

            var payload = JsonHelpers.ToElement(new
            {
                exitCode = cmdRes.ExitCode,
                stdout = cmdRes.Stdout,
                stderr = cmdRes.Stderr,
                stdoutTruncated = cmdRes.StdoutTruncated,
                stderrTruncated = cmdRes.StderrTruncated,
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
        Guid reservation,
        CancellationToken ct)
    {
        try
        {
            var turn = await ExecuteSessionTurnPipelineAsync(subagentSessionId, "wake", prompt, ct, wakeReservation: reservation).ConfigureAwait(false);
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
        finally
        {
            if (!_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(_ =>
                {
                    if (_reservedWakeSessions.TryGetValue(subagentSessionId, out var held) && held == reservation)
                        _reservedWakeSessions.Remove(subagentSessionId);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<TaskDto> CompleteAttemptDoneByAttemptIdAsync(
        string attemptId,
        JsonElement? workerResult,
        CancellationToken ct, WorkerCompletionOrigin? origin = null)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var verificationToken = _actorShutdownCts.Token;
            var prep = await EnqueueAsync<(TaskDto Task, ActiveAttemptState Active, string? AfterWorkerDoneCmd)>(async _ =>
            {
                await EnsureHydratedCoreAsync(verificationToken).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!_activeAttempts.TryGetValue(attemptId, out var active))
                {
                    throw KernelException.Conflict($"Attempt '{attemptId}' is not active on track '{TrackId}'.");
                }

                CheckWorkerCompletionCore(active, origin);
                var task = RequireTaskCore(active.TaskId);
                var now = NowMs();
                var clonedWorkerResult = workerResult?.Clone();

                var verifyingTask = task with { Status = TaskRunStatus.Verifying, UpdatedAtMs = now };
                ReplaceTaskInMemoryCore(verifyingTask);

                var verifyingAttempt = active.AttemptDto with
                {
                    Status = "verifying",
                    WorkerResult = clonedWorkerResult
                };
                active.AttemptDto = verifyingAttempt;

                await PersistAndEmitTaskUpdatedCoreAsync(verifyingTask, verifyingAttempt, nextTemplateState: null, now, verificationToken)
                    .ConfigureAwait(false);

                return (verifyingTask, active, _policy?.Manifest.AfterWorkerDoneCmd);
            }, verificationToken).ConfigureAwait(false);

            var mergedMeta = prep.Active.AttemptDto.Meta;
            var gatePassed = true;
            string? failedStepReason = null;
            JsonElement? gateVerdict = null;

            try
            {
                // 1. Run AfterWorkerDoneCmd (if non-empty) in lease.EffectiveCwd and merge last valid JSON stdout line into attempt.Meta.
                if (!string.IsNullOrWhiteSpace(prep.AfterWorkerDoneCmd))
                {
                    var hookResult = await _commandRunner.RunAsync(
                        new ShellCommandRequest(
                            prep.AfterWorkerDoneCmd!,
                            prep.Active.Lease.EffectiveCwd,
                            TimeSpan.FromSeconds(_options.DefaultHookTimeoutSecs)),
                        verificationToken).ConfigureAwait(false);

                    mergedMeta = JsonHelpers.MergeLastValidJsonLine(mergedMeta, hookResult.Stdout);
                }

                // 2. Run GateSpecDto steps in lease.EffectiveCwd.

                if (prep.Task.Gate is { Steps.Count: > 0 } gateSpec)
                {
                    var stepRecords = new List<object>(gateSpec.Steps.Count);
                    var stepTimeout = TimeSpan.FromSeconds(Math.Max(1, gateSpec.TimeoutSecs));

                    foreach (var step in gateSpec.Steps)
                    {
                        var res = await _commandRunner.RunAsync(
                            new ShellCommandRequest(step.Cmd, prep.Active.Lease.EffectiveCwd, stepTimeout, OutputTailChars: TaskOutputTailChars),
                            verificationToken).ConfigureAwait(false);

                        var stepPassed = res.ExitCode == 0 && !res.TimedOut;
                        var combinedOut = string.IsNullOrEmpty(res.Stderr)
                            ? res.Stdout
                            : string.IsNullOrEmpty(res.Stdout)
                                ? res.Stderr
                                : $"{res.Stdout}\n{res.Stderr}";
                        var outputTail = combinedOut.Length <= 4096
                            ? combinedOut
                            : combinedOut[^4096..];

                        stepRecords.Add(new
                        {
                            name = step.Name,
                            cmd = step.Cmd,
                            passed = stepPassed,
                            exitCode = res.ExitCode,
                            stdout = res.Stdout,
                            stderr = res.Stderr,
                            stdoutTruncated = res.StdoutTruncated,
                            stderrTruncated = res.StderrTruncated,
                            outputTail,
                            timedOut = res.TimedOut,
                            durationMs = res.DurationMs
                        });

                        if (!stepPassed)
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
            }
            catch (Exception ex) when (!verificationToken.IsCancellationRequested)
            {
                gatePassed = false;
                failedStepReason = $"Verification could not finish: {ex.Message}";
                gateVerdict = JsonHelpers.ToElement(new { v = 1, passed = false, failedReason = failedStepReason });
            }

            var disposition = gatePassed ? AttemptLeaseDisposition.Promote : AttemptLeaseDisposition.Discard;

            var (finalTask, wakePrompt) = await EnqueueAsync<(TaskDto FinalTask, string WakePrompt)>(async _ =>
            {
                if (!_activeAttempts.TryGetValue(attemptId, out var current) || !ReferenceEquals(current, prep.Active) ||
                    !string.Equals(current.AttemptDto.Status, "verifying", StringComparison.OrdinalIgnoreCase))
                    throw KernelException.Conflict("The attempt ended while its gate was running.");
                await current.Driver.SettleAttemptLeaseAsync(current.Lease, disposition, verificationToken).ConfigureAwait(false);
                _activeAttempts.Remove(attemptId);
                Volatile.Write(ref _activeAttemptCount, _activeAttempts.Count);
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
                    Status = gatePassed ? "done" : "failed",
                    Meta = mergedMeta,
                    GateVerdict = gateVerdict,
                    FinishedAtMs = now
                };

                await PersistAndEmitTaskUpdatedCoreAsync(settledTask, settledAttempt, nextTemplateState, now, verificationToken)
                    .ConfigureAwait(false);

                await ScheduleEligibleTasksCoreAsync(verificationToken).ConfigureAwait(false);

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
            }, verificationToken).ConfigureAwait(false);

            await WakeLeadAsync("wake", wakePrompt, verificationToken).ConfigureAwait(false);
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
        CancellationToken ct, WorkerCompletionOrigin? origin = null)
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

                CheckWorkerCompletionCore(state, origin);
                state.Settling = true;
                return state;
            }, ct).ConfigureAwait(false);

            var (failedTask, wakePrompt) = await EnqueueAsync<(TaskDto FailedTask, string WakePrompt)>(async _ =>
            {
                if (!_activeAttempts.TryGetValue(attemptId, out var current) || !ReferenceEquals(current, active))
                    throw KernelException.Conflict("The attempt already ended.");
                await active.Driver.SettleAttemptLeaseAsync(active.Lease, AttemptLeaseDisposition.Discard, ct).ConfigureAwait(false);
                _activeAttempts.Remove(attemptId);
                Volatile.Write(ref _activeAttemptCount, _activeAttempts.Count);
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
                    Status = "failed",
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
        CancellationToken ct,
        PersistedSessionInput? input = null, Guid? wakeReservation = null, string? mailId = null,
        SessionMessage? workerInput = null)
    {
        if (input is null)
            await WaitForWorkspacePreparationAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _inFlightExternalOps);
        ActiveTurnState? pendingCleanup = null;
        var recordUserAtStart = input is null;
        try
        {
            // Durable turns already hold the session slot; compact after their durable outcome.
            var needsPreCompact = input is null && wakeReservation is null && await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var s = RequireSessionCore(sessionId);
                return s.BoundAttemptId is null && s.ContextUsageRatio >= _options.AutoCompactionThreshold;
            }, ct).ConfigureAwait(false);

            if (needsPreCompact)
            {
                await CompactSessionInternalAsync(sessionId, ct).ConfigureAwait(false);
            }

            var turnSetup = await EnqueueAsync<(ActiveTurnState? ActiveState, string EffectivePrompt, SessionTurnDto? FailedWake)>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var session = RequireSessionCore(sessionId);
                RequireSessionDispatchAvailableCore(sessionId);
                if (_activeTurns.ContainsKey(sessionId) || _compactingSessions.Contains(sessionId) ||
                    (input is null && HoldsDurableSlot(session)) ||
                    (_reservedWakeSessions.TryGetValue(sessionId, out var reservedWake) && reservedWake != wakeReservation))
                    throw KernelException.Conflict($"Session '{sessionId}' already has an active or unresolved turn.");

                var effectivePrompt = prompt;
                var workerSettings = session.BoundAttemptId is null ? null
                    : await ReadWorkerSettingsCoreAsync(session, ct).ConfigureAwait(false);
                var now = NowMs();
                var turnIndex = input?.TurnIndex ?? NextTurnIndexCore(session.Id);
                var turnId = input?.Receipt.TurnId ?? $"trn_{turnIndex:D6}_{Guid.NewGuid():N}";
                var turnDto = new SessionTurnDto(
                    Id: turnId,
                    SessionId: session.Id,
                    TurnIndex: turnIndex,
                    Epoch: input?.Epoch ?? session.Epoch,
                    TriggerKind: triggerKind,
                    Input: input is null ? JsonHelpers.ToElement(new { prompt, workerSettings }) : JsonHelpers.ToElement(new { }),
                    Status: "running",
                    Usage: null,
                    ErrorText: null,
                    StartedAtMs: now,
                    CompletedAtMs: null);

                var runningSession = session with { Status = "running", UpdatedAtMs = now };
                var runningEvent = SessionUpdatedEvent(runningSession);
                if (input is not null)
                {
                    if (_track.ClosedAtMs is not null)
                        return (null, effectivePrompt, null);
                    await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
                    if (!await _store.SessionInputs.ClaimAsync(input, runningSession, runningEvent, ct).ConfigureAwait(false))
                        return (null, effectivePrompt, null);
                    session = session with { Provider = input.Provider, Model = input.Model, ReasoningEffort = input.ReasoningEffort };
                }

                IAgentSession agentSession;
                try
                {
                    if (session.BoundAttemptId is { } workerAttemptId && RequireCurrentWorkerCore(sessionId, workerAttemptId).Attempt.Paused)
                        throw KernelException.Conflict("The Worker is paused.");
                    if (input is not null || workerInput is not null)
                        RequireDispatchWorkspace(session);

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

                    agentSession = await GetOrOpenAgentSessionCoreAsync(session, overrideSystemPrompt: null, ct)
                        .ConfigureAwait(false);
                    if (workerSettings is not null)
                        workerSettings = ResolveWorkerTurnSettingsCore(session, workerSettings);
                }
                catch when (input is not null)
                {
                    var failed = turnDto with { Status = "failed", ErrorText = "Message could not be prepared for dispatch.", CompletedAtMs = NowMs() };
                    var idle = runningSession with { Status = "idle", UpdatedAtMs = failed.CompletedAtMs.Value };
                    var outcome = new SessionItemDto(0, sessionId, turnId, "turn_outcome",
                        JsonHelpers.ToElement(new { v = 1, status = "failed", error = failed.ErrorText }), idle.UpdatedAtMs);
                    await SettleDurableTurnCoreAsync(input, "rejected", failed, [outcome], idle).ConfigureAwait(false);
                    await SchedulePendingWakeCoreAsync(sessionId).ConfigureAwait(false);
                    return (null, effectivePrompt, null);
                }
                if (recordUserAtStart)
                {
                    var userItem = CreateWakeUserItem(turnDto, prompt, workerInput?.Id);
                    var recorded = await _store.RecordTurnAndItemsWithIdsAsync(turnDto, [userItem], runningSession, ct, workerInput)
                        .ConfigureAwait(false);
                    turnDto = recorded.Turn;
                }

                _sessions[session.Id] = runningSession;
                var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _actorShutdownCts.Token);
                var activeState = new ActiveTurnState(session.Id, turnDto, agentSession, turnCts, input is not null || recordUserAtStart) { Settings = workerSettings };
                _activeTurns[session.Id] = activeState;
                pendingCleanup = activeState;
                if (wakeReservation is not null)
                    _reservedWakeSessions.Remove(session.Id);
                if (input is null)
                    await PersistAndEmitSessionUpdatedCoreAsync(runningSession, now, ct).ConfigureAwait(false);
                else
                    await PublishInputEventAsync(runningEvent).ConfigureAwait(false);
                return (activeState, effectivePrompt, null);
            }, _actorShutdownCts.Token).ConfigureAwait(false);

            if (turnSetup.FailedWake is { } failedWake)
                return failedWake;

            if (turnSetup.ActiveState is not { } activeTurn)
                return new SessionTurnDto(input!.Receipt.TurnId, sessionId, input.TurnIndex, input.Epoch, triggerKind,
                    JsonHelpers.ToElement(new { }), "queued", null, null, null, null);

            var items = new List<SessionItemDto>();
            var textAccum = new StringBuilder();
            var thinkingAccum = new StringBuilder();
            TokenUsage? finalUsage = null;
            string? boundThreadId = null;
            string? errorText = null;
            var turnStatus = "completed";

            var delivered = false;
            var rejectedBeforePrompt = false;
            var conclusive = false;

            if (input is null && !recordUserAtStart)
                items.Add(CreateWakeUserItem(activeTurn.TurnDto, prompt, workerInput?.Id));

            async Task RecordActivityAsync(SessionItemDto item)
            {
                if (input is null && !recordUserAtStart)
                    items.Add(item);
                else
                    await _store.AppendSessionItemsAsync(sessionId, [item], CancellationToken.None).ConfigureAwait(false);
            }

            async Task FlushThinkingChunkAsync(long createdAtMs)
            {
                if (thinkingAccum.Length == 0)
                {
                    return;
                }

                var chunk = thinkingAccum.ToString();
                thinkingAccum.Clear();
                var thinkingItem = new SessionItemDto(
                    Id: 0,
                    SessionId: sessionId,
                    TurnId: activeTurn.TurnDto.Id,
                    Kind: "thinking",
                    Payload: JsonHelpers.ToElement(new { v = 1, text = chunk }),
                    CreatedAtMs: createdAtMs);
                await RecordActivityAsync(thinkingItem).ConfigureAwait(false);
            }

            try
            {
                await foreach (var evt in activeTurn.AgentSession
                                   .StreamTurnAsync(new TurnInput(turnSetup.EffectivePrompt, activeTurn.TurnDto.Id,
                                       activeTurn.Settings?.Model, activeTurn.Settings?.ReasoningEffort), activeTurn.TurnCts.Token)
                                   .ConfigureAwait(false))
                {
                    var evtNow = NowMs();
                    var confirmsDelivery = evt is UnifiedAgentEvent.TurnAccepted or UnifiedAgentEvent.TextDelta or
                        UnifiedAgentEvent.ThinkingDelta or UnifiedAgentEvent.ToolCallStarted or
                        UnifiedAgentEvent.ToolCallCompleted or UnifiedAgentEvent.ApprovalRequested or
                        UnifiedAgentEvent.TurnCompleted or UnifiedAgentEvent.TurnInterrupted or
                        UnifiedAgentEvent.TurnError { IsConclusive: true, BeforePrompt: false };
                    if (mailId is not null && confirmsDelivery)
                    {
                        await _store.AcknowledgeMailWakeAsync(mailId, CancellationToken.None).ConfigureAwait(false);
                        mailId = null;
                    }
                    if (input is not null && ((!delivered && confirmsDelivery) || evt is UnifiedAgentEvent.TurnAccepted))
                    {
                        await _store.SessionInputs.MarkDeliveredAsync(input, boundThreadId,
                            (evt as UnifiedAgentEvent.TurnAccepted)?.NativeTurnId, CancellationToken.None).ConfigureAwait(false);
                        delivered = true;
                    }
                    if (workerInput is not null && !delivered && confirmsDelivery)
                    {
                        await Messages.TransitionAsync(workerInput with { TurnId = activeTurn.TurnDto.Id,
                            DeliveredTurnId = activeTurn.TurnDto.Id, Delivery = "delivered" }, "dispatching",
                            CancellationToken.None).ConfigureAwait(false);
                        delivered = true;
                    }
                    if (confirmsDelivery && !activeTurn.ProviderAccepted)
                    {
                        await EnqueueAsync(_ =>
                        {
                            activeTurn.ProviderAccepted = true;
                            if (input is not null || workerInput is not null)
                                NotifyQueueChangedCore(sessionId);
                            return Task.CompletedTask;
                        }, _actorShutdownCts.Token).ConfigureAwait(false);
                    }
                    switch (evt)
                    {
                        case UnifiedAgentEvent.ThreadBound tb:
                            boundThreadId = tb.NativeThreadId;
                            break;

                        case UnifiedAgentEvent.TextDelta td:
                            await FlushThinkingChunkAsync(evtNow).ConfigureAwait(false);
                            textAccum.Append(td.Delta);
                            PublishTurnActivity(activeTurn,
                                KernelEventKinds.SessionTextDelta,
                                JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, delta = td.Delta }),
                                evtNow);
                            break;

                        case UnifiedAgentEvent.ThinkingDelta thd:
                            thinkingAccum.Append(thd.Delta);
                            PublishTurnActivity(activeTurn,
                                KernelEventKinds.SessionThinkingDelta,
                                JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, delta = thd.Delta }),
                                evtNow);
                            break;

                        case UnifiedAgentEvent.ToolCallStarted tcs:
                            await FlushThinkingChunkAsync(evtNow).ConfigureAwait(false);
                            PublishTurnActivity(activeTurn,
                                KernelEventKinds.SessionToolProgress,
                                JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, phase = "started", callId = tcs.CallId, name = tcs.Name, args = tcs.Args }),
                                evtNow);
                            await RecordActivityAsync(new SessionItemDto(
                                Id: 0,
                                SessionId: sessionId,
                                TurnId: activeTurn.TurnDto.Id,
                                Kind: "tool_call",
                                Payload: JsonHelpers.ToElement(new { v = 1, callId = tcs.CallId, name = tcs.Name, args = tcs.Args }),
                                CreatedAtMs: evtNow)).ConfigureAwait(false);
                            break;

                        case UnifiedAgentEvent.ToolCallCompleted tcc:
                            await FlushThinkingChunkAsync(evtNow).ConfigureAwait(false);
                            PublishTurnActivity(activeTurn,
                                KernelEventKinds.SessionToolProgress,
                                JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, phase = "completed", callId = tcc.CallId, isError = tcc.IsError, output = activeTurn.Settings is null ? null : tcc.Output }),
                                evtNow);
                            await RecordActivityAsync(new SessionItemDto(
                                Id: 0,
                                SessionId: sessionId,
                                TurnId: activeTurn.TurnDto.Id,
                                Kind: "tool_result",
                                Payload: JsonHelpers.ToElement(new { v = 1, callId = tcc.CallId, output = tcc.Output, isError = tcc.IsError }),
                                CreatedAtMs: evtNow)).ConfigureAwait(false);
                            break;

                        case UnifiedAgentEvent.ApprovalRequested approval:
                            if (activeTurn.Settings?.ApprovalMode == "deny")
                            {
                                await activeTurn.AgentSession.RespondApprovalAsync(approval.RequestId, false,
                                    "The Worker approval mode denies this request.", activeTurn.TurnCts.Token).ConfigureAwait(false);
                                break;
                            }
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
                            conclusive = true;
                            finalUsage = tc.Usage;
                            break;

                        case UnifiedAgentEvent.TurnInterrupted:
                            conclusive = true;
                            turnStatus = "interrupted";
                            errorText = "Provider confirmed the interruption.";
                            break;

                        case UnifiedAgentEvent.TurnError te:
                            rejectedBeforePrompt = te.BeforePrompt;
                            conclusive = te.IsConclusive;
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
            catch (OperationCanceledException) when (input is null && ct.IsCancellationRequested)
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

            if (activeTurn.Interrupted && turnStatus == "completed")
            {
                turnStatus = "interrupted";
                errorText = "Turn interrupted.";
            }

            if (mailId is { } pendingMailId && activeTurn.Interrupted)
                await _store.AcknowledgeMailWakeAsync(pendingMailId, CancellationToken.None).ConfigureAwait(false);

            if (input is not null && !conclusive)
            {
                turnStatus = "unknown";
                errorText ??= "Provider outcome could not be confirmed.";
            }

            if (workerInput is not null && !delivered)
                await Messages.TransitionAsync(workerInput with { TurnId = activeTurn.TurnDto.Id,
                    Delivery = turnStatus == "interrupted" ? "interrupted" : rejectedBeforePrompt ? "rejected" : "unknown" },
                    "dispatching", CancellationToken.None).ConfigureAwait(false);

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

            if (turnStatus is "failed" or "interrupted")
            {
                items.Add(new SessionItemDto(
                    Id: 0,
                    SessionId: sessionId,
                    TurnId: activeTurn.TurnDto.Id,
                    Kind: "turn_outcome",
                    Payload: JsonHelpers.ToElement(new
                    {
                        v = 1,
                        status = turnStatus,
                        error = errorText ?? (turnStatus == "interrupted" ? "Turn interrupted." : "Turn failed.")
                    }),
                    CreatedAtMs: endNow));
            }

            var finalizeOutcome = await EnqueueAsync<(SessionTurnDto CompletedTurn, bool ShouldAutoCompact)>(async _ =>
            {
                var currentSession = RequireSessionCore(sessionId);
                if (currentSession.BoundAttemptId is { } boundAttemptId && activeTurn.Interrupted &&
                    _activeAttempts.TryGetValue(boundAttemptId, out var pausedAttempt) && pausedAttempt.Paused)
                {
                    turnStatus = "interrupted";
                    errorText = "Turn interrupted.";
                    await CloseInterruptedWorkerCoreAsync(activeTurn, pausedAttempt).ConfigureAwait(false);
                }

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
                    Status = turnStatus == "unknown" && input is not null ? "unknown" : "idle",
                    ContextUsageRatio = nextRatio,
                    UpdatedAtMs = endNow
                };
                var completedTurn = activeTurn.TurnDto with
                {
                    Input = activeTurn.Settings is null ? activeTurn.TurnDto.Input :
                        JsonHelpers.ToElement(new { prompt, workerSettings = activeTurn.ProviderAccepted ? activeTurn.Settings : null }),
                    Status = turnStatus,
                    Usage = finalUsage is null ? null : JsonHelpers.ToElement(finalUsage),
                    ErrorText = errorText,
                    CompletedAtMs = turnStatus == "unknown" ? null : endNow
                };

                if (!conclusive && input?.IsQueuedFollowUp == true && !_sessionResumeHolds.ContainsKey(sessionId))
                    await CreateSessionResumeHoldCoreAsync(sessionId, completedTurn.Id,
                        updatedSession.NativeThreadId ?? string.Empty, CancellationToken.None).ConfigureAwait(false);

                if (conclusive && _sessionResumeHolds.TryGetValue(sessionId, out var recovery) &&
                    recovery.TurnId == completedTurn.Id)
                    await MarkSessionResumeQuiescentCoreAsync(sessionId, recovery, CancellationToken.None).ConfigureAwait(false);

                if (input is not null)
                {
                    await SettleDurableTurnCoreAsync(input, delivered ? "delivered" : "unknown",
                        completedTurn, items, updatedSession).ConfigureAwait(false);
                }
                else
                {
                    await CompleteWakeTurnCoreAsync(completedTurn, items, updatedSession).ConfigureAwait(false);
                }

                _activeTurns.Remove(sessionId);
                activeTurn.Settled.TrySetResult(conclusive);
                var autoCompact = !_sessionResumeHolds.ContainsKey(sessionId) && _sessions[sessionId].BoundAttemptId is null &&
                                  _sessions[sessionId].Status == "idle" && turnStatus == "completed" &&
                                  nextRatio >= _options.AutoCompactionThreshold;

                return (completedTurn, autoCompact);
            }, CancellationToken.None).ConfigureAwait(false);

            if (finalizeOutcome.ShouldAutoCompact)
            {
                try
                {
                    await CompactSessionInternalAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (KernelException ex) when (ex.Code == KernelErrorCode.Conflict)
                {
                    // A new turn reserved the session before optional compaction.
                }
            }

            await EnqueueAsync(async _ =>
            {
                await SchedulePendingWakeCoreAsync(sessionId, allowWorker: turnStatus == "completed").ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);

            return finalizeOutcome.CompletedTurn;
        }
        finally
        {
            if (pendingCleanup is { } active)
            {
                try
                {
                    if (!_actorShutdownCts.IsCancellationRequested)
                        await EnqueueAsync(async _ =>
                        {
                            if (_activeTurns.TryGetValue(sessionId, out var current) && ReferenceEquals(current, active))
                            {
                                _activeTurns.Remove(sessionId);
                                if (input is null)
                                {
                                    var idle = RequireSessionCore(sessionId) with { Status = "idle", UpdatedAtMs = NowMs() };
                                    await PersistAndEmitSessionUpdatedCoreAsync(idle, idle.UpdatedAtMs, CancellationToken.None)
                                        .ConfigureAwait(false);
                                    _sessions[sessionId] = idle;
                                    await SchedulePendingWakeCoreAsync(sessionId).ConfigureAwait(false);
                                }
                            }
                        }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    active.Settled.TrySetResult(false);
                    active.TurnCts.Dispose();
                }
            }
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private void RequireDispatchWorkspace(AgentSessionDto session)
    {
        if ((session.BoundTaskId is not null || session.BoundAttemptId is not null) &&
            (session.BoundAttemptId is not { } attemptId || !_activeAttempts.ContainsKey(attemptId)))
            throw KernelException.Conflict("The task attempt workspace is no longer available.");
    }

    private static SessionItemDto CreateWakeUserItem(SessionTurnDto turn, string prompt, string? inputId, string? deliveryError = null) =>
        new(Id: 0, SessionId: turn.SessionId, TurnId: turn.Id, Kind: "user",
            Payload: inputId is null
                ? JsonHelpers.ToElement(new { v = 1, content = prompt, triggerKind = turn.TriggerKind })
                : JsonHelpers.ToElement(new { v = 1, content = prompt, triggerKind = turn.TriggerKind, inputId, deliveryError }),
            CreatedAtMs: turn.StartedAtMs!.Value);

    private async Task CompleteWakeTurnCoreAsync(SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
        AgentSessionDto session)
    {
        var ct = CancellationToken.None;
        var turnEvent = TurnCompletedEvent(turn, session.UpdatedAtMs);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.RecordTurnAndItemsAsync(turn, items, session, ct).ConfigureAwait(false);
        _sessions[session.Id] = session;
        await _store.AppendEventAsync(turnEvent, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, turnEvent, ct).ConfigureAwait(false);
        var sessionEvent = SessionUpdatedEvent(session);
        await _store.AppendEventAsync(sessionEvent, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, sessionEvent, ct).ConfigureAwait(false);
    }

    private async Task<AgentSessionDto> CompactSessionInternalAsync(string sessionId, CancellationToken ct)
    {
        Interlocked.Increment(ref _inFlightExternalOps);
        var reserved = false;
        try
        {
            var prep = await EnqueueAsync<(AgentSessionDto Session, IAgentSession OldAgentSession, int HandoverTurnIndex)>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var session = RequireSessionCore(sessionId);
                if (session.BoundAttemptId is not null)
                    throw KernelException.Conflict("Worker sessions do not support a manual handover turn.");
                RequireSessionDispatchAvailableCore(sessionId);
                if (_activeTurns.ContainsKey(sessionId) || HoldsDurableSlot(session) || _compactingSessions.Contains(sessionId) ||
                    _reservedWakeSessions.ContainsKey(sessionId))
                {
                    throw KernelException.Conflict($"Cannot compact session '{sessionId}' while a turn is actively running.");
                }

                var oldAgentSession = await GetOrOpenAgentSessionCoreAsync(session, overrideSystemPrompt: null, ct)
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                _compactingSessions.Add(sessionId);
                reserved = true;
                var turnIdx = NextTurnIndexCore(sessionId);
                return (session, oldAgentSession, turnIdx);
            }, CancellationToken.None).ConfigureAwait(false);

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
                _boundAgentModels.Remove(sessionId);
                _openAgentSessionPermissionModes.Remove(sessionId);
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
                var desiredMode = compacted.PermissionMode;
                var freshAgentSession = await OpenFreshAgentSessionCoreAsync(compacted, seedPrompt, ct).ConfigureAwait(false);
                _openAgentSessions[sessionId] = freshAgentSession;
                _boundAgentModels[sessionId] = (compacted.Model, compacted.ReasoningEffort);
                _openAgentSessionPermissionModes[sessionId] = desiredMode;

                return compacted;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            if (reserved && !_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(async _ =>
                {
                    _compactingSessions.Remove(sessionId);
                    await SchedulePendingWakeCoreAsync(sessionId).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private async Task<IAgentSession> GetOrOpenAgentSessionCoreAsync(
        AgentSessionDto session,
        string? overrideSystemPrompt,
        CancellationToken ct)
    {
        var desiredMode = session.PermissionMode;

        if (_openAgentSessions.TryGetValue(session.Id, out var existing))
        {
            var samePermission = !_openAgentSessionPermissionModes.TryGetValue(session.Id, out var openMode) ||
                string.Equals(openMode, desiredMode, StringComparison.Ordinal);
            if (samePermission && _boundAgentModels.TryGetValue(session.Id, out var bound) &&
                string.Equals(bound.Model, session.Model, StringComparison.Ordinal) &&
                string.Equals(bound.ReasoningEffort, session.ReasoningEffort, StringComparison.Ordinal))
            {
                return existing;
            }

            var switched = false;
            try
            {
                switched = samePermission && await existing.TrySetModelAsync(session.Model, session.ReasoningEffort, ct).ConfigureAwait(false);
            }
            catch
            {
                _openAgentSessions.Remove(session.Id);
                _boundAgentModels.Remove(session.Id);
                _openAgentSessionPermissionModes.Remove(session.Id);
                await existing.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            if (switched)
            {
                _boundAgentModels[session.Id] = (session.Model, session.ReasoningEffort);
                return existing;
            }

            _openAgentSessions.Remove(session.Id);
            _boundAgentModels.Remove(session.Id);
            _openAgentSessionPermissionModes.Remove(session.Id);
            await existing.DisposeAsync().ConfigureAwait(false);
        }

        var opened = await OpenFreshAgentSessionCoreAsync(session, overrideSystemPrompt, ct).ConfigureAwait(false);
        _openAgentSessions[session.Id] = opened;
        _boundAgentModels[session.Id] = (session.Model, session.ReasoningEffort);
        _openAgentSessionPermissionModes[session.Id] = desiredMode;
        return opened;
    }

    private async Task<IAgentSession> OpenFreshAgentSessionCoreAsync(
        AgentSessionDto session,
        string? overrideSystemPrompt,
        CancellationToken ct)
    {
        var spec = await BuildAgentLaunchSpecCoreAsync(session, overrideSystemPrompt, ct).ConfigureAwait(false);
        return await _agentRuntime.OpenSessionAsync(spec, ct).ConfigureAwait(false);
    }

    private async Task<HarnessLaunchSpec> BuildAgentLaunchSpecCoreAsync(
        AgentSessionDto session, string? overrideSystemPrompt, CancellationToken ct)
    {
        if (session.BoundAttemptId is { } attemptId && !_activeAttempts.ContainsKey(attemptId))
            throw KernelException.Conflict("The task attempt workspace is no longer available.");
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
        return new HarnessLaunchSpec(
            Provider: session.Provider,
            Model: session.Model,
            Cwd: effectiveCwd,
            ResumeNativeThreadId: session.NativeThreadId,
            SystemPrompt: systemPrompt,
            McpServers: mcpReg.McpServers,
            Sandbox: sandboxMode,
            OverlayMounts: overlayMounts,
            ReasoningEffort: session.ReasoningEffort,
            StateKey: session.Id,
            ConfigureWorker: session.BoundAttemptId is not null,
            PermissionMode: session.PermissionMode);
    }

    private async Task DispatchLeadWakePromptIfNeededAsync(string triggerKind, string wakePrompt, CancellationToken ct, string? mailId = null, bool recovering = false)
    {
        var check = await EnqueueAsync<(string? LeadSessionId, Guid? Reservation)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_track.ClosedAtMs is not null || string.IsNullOrEmpty(_track.LeadSessionId))
            {
                if (mailId is not null)
                    await _store.AcknowledgeMailWakeAsync(mailId, ct).ConfigureAwait(false);
                return (null, null);
            }

            var leadId = _track.LeadSessionId!;
            if (!_sessions.ContainsKey(leadId))
            {
                return (null, null);
            }

            if (!WorkspaceReady || _activeTurns.ContainsKey(leadId) || HoldsDurableSlot(_sessions[leadId]) ||
                _compactingSessions.Contains(leadId) || _reservedWakeSessions.ContainsKey(leadId) ||
                _sessionResumeHolds.ContainsKey(leadId))
            {
                EnqueuePendingWakeCore(leadId, triggerKind, wakePrompt, mailId);
                return (leadId, null);
            }

            ct.ThrowIfCancellationRequested();
            var reservation = Guid.NewGuid();
            _reservedWakeSessions.Add(leadId, reservation);
            return (leadId, reservation);
        }, CancellationToken.None).ConfigureAwait(false);

        if (check.LeadSessionId is null || check.Reservation is not { } reservation)
        {
            return;
        }

        if (recovering || _options.DispatchWakesInBackground)
        {
            _ = TrackBackgroundJob(() => RunReservedWakeAsync(check.LeadSessionId, triggerKind, wakePrompt, reservation, _actorShutdownCts.Token, mailId));
        }
        else
        {
            await RunReservedWakeAsync(check.LeadSessionId, triggerKind, wakePrompt, reservation, ct, mailId).ConfigureAwait(false);
        }
    }

    private async Task RunReservedWakeAsync(string sessionId, string kind, string prompt, Guid reservation, CancellationToken ct, string? mailId)
    {
        try
        {
            await ExecuteSessionTurnPipelineAsync(sessionId, kind, prompt, ct, wakeReservation: reservation, mailId: mailId).ConfigureAwait(false);
        }
        finally
        {
            if (!_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(async _ =>
                {
                    if (_reservedWakeSessions.TryGetValue(sessionId, out var current) && current == reservation)
                    {
                        _reservedWakeSessions.Remove(sessionId);
                        await SchedulePendingWakeCoreAsync(sessionId).ConfigureAwait(false);
                    }
                }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed record PendingWake(string TriggerKind, string Prompt, string? MailId);

    private void EnqueuePendingWakeCore(string sessionId, string triggerKind, string prompt, string? mailId = null)
    {
        if (!_pendingWakes.TryGetValue(sessionId, out var queue))
        {
            queue = new Queue<PendingWake>();
            _pendingWakes[sessionId] = queue;
        }

        if (mailId is not null)
            Interlocked.Increment(ref _queuedMailWakes);
        queue.Enqueue(new PendingWake(triggerKind, prompt, mailId));
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

    private async Task<PendingAskSuspension?> ApplyGuardDecisionCoreAsync(
        GuardDecision decision, string? callerSessionId, CancellationToken ct) =>
        decision switch
        {
            GuardDecision.Allow => null,
            GuardDecision.Deny deny => throw KernelException.PolicyDenied(deny.Reason),
            GuardDecision.AskHuman ask => await CreateAskSuspensionCoreAsync(
                ask.Title, ask.Options, callerSessionId, ct).ConfigureAwait(false)
        };

    private async Task<PendingAskSuspension> CreateAskSuspensionCoreAsync(
        string title,
        IReadOnlyList<string> options,
        string? callerSessionId,
        CancellationToken ct)
    {
        var effectiveOptions = options.Count > 0 ? options : ["Approve", "Deny"];
        var questions = new[] { new AskQuestionDto(title, effectiveOptions, IsMultiSelect: false) };
        return await CreateAskSuspensionFromQuestionsCoreAsync(questions, callerSessionId, ct, AskDelivery.Hold).ConfigureAwait(false);
    }

    private async Task<PendingAskSuspension> CreateAskSuspensionFromQuestionsCoreAsync(
        IReadOnlyList<AskQuestionDto> questions,
        string? callerSessionId,
        CancellationToken ct,
        AskDelivery delivery = AskDelivery.Wake)
    {
        var now = NowMs();
        var ask = new AskDto(
            Id: $"ask_{Guid.NewGuid():N}",
            TrackId: TrackId,
            Status: AskStatus.Open,
            Questions: questions,
            Answer: null,
            CreatedAtMs: now,
            AnsweredAtMs: null,
            Delivery: delivery,
            SessionId: callerSessionId,
            TurnId: callerSessionId is not null && _activeTurns.TryGetValue(callerSessionId, out var owner) ? owner.TurnDto.Id : null);

        if (delivery == AskDelivery.Hold && callerSessionId is null)
            throw KernelException.Forbidden("An approval requires a session.");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        _asks[ask.Id] = ask;
        _pendingAskWaiters[ask.Id] = tcs;
        // Request cancellation expires approval authority even if the ask stays visible.
        if (ct.CanBeCanceled)
            _ = ct.Register(() => tcs.TrySetCanceled(ct));

        var options = questions[0].Options.Count > 0 ? questions[0].Options : ["Approve", "Deny"];
        var callerIsLead = callerSessionId is not null &&
                           string.Equals(callerSessionId, _track.LeadSessionId, StringComparison.Ordinal);

        // If a non-lead session initiated an ask, notify Lead only if desired? No, Ask is for human UI; Lead wakes when answered!
        _ = callerIsLead;
        return new PendingAskSuspension(ask, options, tcs.Task, ShouldWakeLead: false, LeadWakePrompt: null);
    }

    private static void RequireAttemptsOutsideVerification(IEnumerable<ActiveAttemptState> attempts)
    {
        if (attempts.Any(attempt => string.Equals(attempt.AttemptDto.Status, "verifying", StringComparison.OrdinalIgnoreCase)))
            throw KernelException.Conflict("Wait for the current verification to finish.");
    }

    private async Task<TaskAttemptDto> CancelAndDiscardActiveAttemptCoreAsync(
        ActiveAttemptState active,
        string reason,
        long now,
        CancellationToken ct)
    {
        RequireAttemptsOutsideVerification([active]);
        _activeAttempts.Remove(active.AttemptId);
        Volatile.Write(ref _activeAttemptCount, _activeAttempts.Count);
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
            Status = "canceled",
            FinishedAtMs = now
        };
    }

    private async Task CancelAllInFlightAttemptsCoreAsync(string reason, CancellationToken ct)
    {
        RequireAttemptsOutsideVerification(_activeAttempts.Values);
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
        _sessions[session.Id] = session;
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
    }

    private EventEnvelopeDto CreateTaskUpdatedEventCore(long now) =>
        new(null, _track.AreaId, TrackId, KernelEventKinds.TaskUpdated, JsonHelpers.EmptyObject, now);

    private async Task PersistAndEmitTaskUpdatedCoreAsync(
        TaskDto task,
        TaskAttemptDto? attempt,
        JsonElement? nextTemplateState,
        long now,
        CancellationToken ct)
    {
        var evt = CreateTaskUpdatedEventCore(now);
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
            TemplateSnapshotSource: _templateSnapshotSource,
            WorkspacePreparation: WorkspacePreparationStatus);
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

    private string? GetDefaultModelCore(string provider)
    {
        if (_track.LeadSessionId is { } leadId &&
            _sessions.TryGetValue(leadId, out var lead) &&
            string.Equals(lead.Provider, provider, StringComparison.OrdinalIgnoreCase))
        {
            return lead.Model;
        }

        return string.Equals(_options.DefaultProvider, provider, StringComparison.OrdinalIgnoreCase)
            ? _options.DefaultModel
            : null;
    }

    private int NextTurnIndexCore(string sessionId)
    {
        _nextTurnIndexBySession.TryGetValue(sessionId, out var current);
        var next = current + 1;
        _nextTurnIndexBySession[sessionId] = next;
        return next;
    }

    private string BuildDefaultSystemPromptCore(AgentSessionDto session)
    {
        var reportMd = _reportEngine.RenderMarkdown(_report, withMarkers: true);
        var role = session.IsLead
            ? "Lead Session (Primary Track Representative)"
            : session.BoundAttemptId is not null
                ? $"Contract Subagent (BoundAttemptId={session.BoundAttemptId})"
                : "Collaborative Subagent (Unbound)";

        var templateNote = session.IsLead && _policy is null
            ? "\n\nTemplates are optional and implicit. If useful, discover them with perga_template_ls, inspect a template or the authoring contract with perga_template_cat, and apply one with perga_track_set using templateId or custom .perga.js source (plus optional inputs)."
            : string.Empty;
        return $"You are operating in Perga Track '{_track.Title}' ({TrackId}) as {role}.{templateNote}\n\n## Current Report\n{reportMd}";
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

    private Task TrackBackgroundJob(Func<Task> jobFactory)
    {
        var jobId = Guid.NewGuid();
        var task = Task.Run(jobFactory);
        _backgroundJobs[jobId] = task;
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _backgroundJobs.TryRemove(jobId, out _);
            TouchActivity();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
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
            _boundAgentModels.Remove(kvp.Key);
            _openAgentSessionPermissionModes.Remove(kvp.Key);
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
        CancellationTokenSource turnCts,
        bool hasStoredTurn)
    {
        public bool CanSteerQueuedMessages => hasStoredTurn && AgentSession.SupportsNativeSteer;
        public string SessionId { get; } = sessionId;
        public SessionTurnDto TurnDto { get; } = turnDto;
        public IAgentSession AgentSession { get; } = agentSession;
        public CancellationTokenSource TurnCts { get; } = turnCts;
        public HashSet<string> OwnedAskIds { get; } = new(StringComparer.Ordinal);
        public TurnActivity Activity { get; } = new();
        public WorkerSettings? Settings { get; init; }
        public bool Interrupted { get; set; }
        public bool ProviderAccepted { get; set; }
        public TaskCompletionSource<bool> Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        public bool Paused { get; set; }
        public bool InterruptionUnconfirmed { get; set; }
        public bool Settling { get; set; }
        public string? LastError { get; set; }
        public WorkerProviderConfiguration? ProviderConfiguration { get; set; }
    }

    private sealed record PendingAskSuspension(
        AskDto Ask,
        IReadOnlyList<string> Options,
        Task<JsonElement> WaiterTask,
        bool ShouldWakeLead,
        string? LeadWakePrompt);
}
