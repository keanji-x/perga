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
    private readonly IScheduleCronEvaluator? _scheduleCronEvaluator;

    private readonly Channel<Func<TrackActor, Task>> _mailbox;
    private readonly CancellationTokenSource _actorShutdownCts = new();
    private readonly Task _mailboxLoopTask;

    // Actor-owned mutable state (mutated strictly on the single-writer mailbox loop)
    private bool _hydrated;
    private TrackDto _track = null!;
    private ReportDocument _report = null!;
    private CompiledPolicy? _policy;
    private string? _policyUnavailableReason;
    private string _policyGeneration = Guid.NewGuid().ToString("N");
    private string? _templateSnapshotSource;
    private MountedWorkspace? _mountedWorkspace;
    private readonly List<TaskDto> _tasks = [];
    private readonly Dictionary<string, AgentSessionDto> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AskDto> _asks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalDto> _terminals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextTurnIndexBySession = new(StringComparer.Ordinal);
    private sealed record OpenSessionHandle(IAgentSession Agent, HarnessLaunchSpec LaunchSpec, SessionConfiguration Selection, ConfirmedConfiguration? Confirmation);
    private readonly Dictionary<string, OpenSessionHandle> _openSessionHandles = new(StringComparer.Ordinal);
    private readonly SessionTurnScheduler _scheduler;
    private readonly SessionTurnRunner _turnRunner;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingAskWaiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveAttemptState> _activeAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionIdsByIdempotencyKey = new(StringComparer.Ordinal);
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
        TimeProvider timeProvider, IScheduleCronEvaluator? scheduleCronEvaluator)
    {
        TrackId = trackId;
        _scheduler = new SessionTurnScheduler(this);
        _turnRunner = new SessionTurnRunner(this);
        _store = store;
        _scheduleCronEvaluator = scheduleCronEvaluator;
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

    private int _activeAttemptCount;

    public bool HasActiveWork =>
        Volatile.Read(ref _activeAttemptCount) > 0 ||
        Volatile.Read(ref _sessionResumeHoldCount) > 0 ||
        Volatile.Read(ref _queuedMailboxOps) > 0 ||
        Volatile.Read(ref _inFlightExternalOps) > 0 ||
        !_backgroundJobs.IsEmpty;

    public bool IsSessionOrTaskRunning =>
        _scheduler.HasActiveTurns || _activeAttempts.Count > 0 || _scheduler.HasDispatchingInputs;

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
                Track: PresentTrackCore(),
                Sessions: OrderedSessionsCore(),
                Tasks: _tasks.ToArray(),
                OpenAsks: _asks.Values.Where(a => a.Status == AskStatus.Open).OrderBy(a => a.CreatedAtMs).ToArray(),
                Terminals: _terminals.Values.OrderBy(t => t.CreatedAtMs).ToArray(),
                UnreadMailCount: unreadMails.Count) { PendingApprovals = ReadPendingApprovals() };
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
                SetApprovalAdmissionClosed(nextTrack.ClosedAtMs is not null || _deletingTrack, stopPendingTurns: nextTrack.ClosedAtMs is not null);
                await EmitTrackUpdatedCoreAsync(now, reopening ? _actorShutdownCts.Token : ct).ConfigureAwait(false);
            }

            if (req.Closed is false)
            {
                StartWorkspacePreparationCore(retry: false);
                await ResumeWorkspaceWorkCoreAsync(_actorShutdownCts.Token).ConfigureAwait(false);
            }

            return PresentTrackCore();
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
            return PresentTrackCore();
        }, ct);
    }

    public async Task<TrackDto> SetTemplateBySessionAsync(string callerSessionId, string? templateId,
        string? source, string expectedPolicyGeneration, JsonElement? inputs, CancellationToken ct = default) =>
        (await SetTemplateWithOptionsAsync(callerSessionId, templateId, source, expectedPolicyGeneration, inputs, false, false, ct).ConfigureAwait(false)).Track;

    public Task<TemplateSetResult> SetTemplateWithOptionsAsync(string callerSessionId, string? templateId,
        string? source, string expectedPolicyGeneration, JsonElement? inputs, bool dryRun, bool clear, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, KernelMcpToolNames.TrackSet);
            if (_policyGeneration != expectedPolicyGeneration)
                throw KernelException.PolicyDenied("Active policy changed after template application was authorized.");
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot set a template on a closed track.");
            if (clear && (templateId is not null || source is not null || inputs is not null))
                throw KernelException.InvalidArgument("clear cannot be combined with a template or inputs.");
            if (clear)
            {
                return new TemplateSetResult(await DetachTemplateCoreAsync(dryRun, ct).ConfigureAwait(false), [], dryRun);
            }

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
                TemplateSchedules.ValidateInputs(_policyVm, policy, nextInputs);
                var init = _policyVm.InitState(policy, nextInputs);
                state = (init.Output.ValueKind != JsonValueKind.Undefined ? init.Output : init.NextState).Clone();
            }
            catch (KernelException) { throw; }
            catch (Exception ex)
            {
                throw KernelException.InvalidArgument($"Template compilation or initialization failed: {ex.Message}");
            }

            var warnings = await TemplateWarningsCoreAsync(policy, nextInputs, ct).ConfigureAwait(false);
            var now = NowMs();
            var nextTrack = _track with
            {
                TemplateId = policy.TemplateId,
                TemplateInputs = nextInputs,
                TemplateState = state,
                EnabledPlugins = _track.EnabledPlugins.Concat(policy.Manifest.DefaultPlugins).Distinct(StringComparer.Ordinal).ToArray(),
                UpdatedAtMs = now
            };
            var schedules = TemplateSchedules.Materialize(policy, nextInputs, TrackId, now, _policyVm, _scheduleCronEvaluator);
            if (dryRun) return new TemplateSetResult(TemplatePresentation.Project(nextTrack, policy, _policyVm), warnings, true);
            var evt = new EventEnvelopeDto(null, nextTrack.AreaId, TrackId, KernelEventKinds.TrackUpdated,
                JsonHelpers.ToElement(TemplatePresentation.Project(nextTrack, policy, _policyVm)), now);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.UpdateTrackTemplateAndEmitAsync(nextTrack, policy.JsSource, evt, ct, schedules).ConfigureAwait(false);
            _policy = policy;
            _policyUnavailableReason = null;
            _policyGeneration = Guid.NewGuid().ToString("N");
            _templateSnapshotSource = policy.JsSource;
            var pluginsChanged = !_track.EnabledPlugins.SequenceEqual(nextTrack.EnabledPlugins, StringComparer.Ordinal);
            _track = nextTrack;
            if (pluginsChanged)
                _mcpBridge.UpdateTrackPlugins(TrackId, nextTrack.EnabledPlugins);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
            return new TemplateSetResult(PresentTrackCore(), warnings, false);
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
            SetApprovalAdmissionClosed(true, stopPendingTurns: true);
            await EmitTrackUpdatedCoreAsync(now, ct).ConfigureAwait(false);
            return PresentTrackCore();
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
                    SetApprovalAdmissionClosed(_track.ClosedAtMs is not null);
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
            if (_scheduler.ResumeHolds.Any(hold => hold.Busy))
                throw KernelException.Conflict("Wait for provider recovery before deleting this Track.");
            ct.ThrowIfCancellationRequested();
            _deletingTrack = true;
            SetApprovalAdmissionClosed(true);
            reserve();

            // 1. Cancel active turns
            foreach (var turn in _scheduler.ActiveTurns.ToArray())
            {
                AbortTurn(turn);
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
            foreach (var kvp in _openSessionHandles.ToArray())
            {
                _openSessionHandles.Remove(kvp.Key);

                await kvp.Value.Agent.DisposeAsync().ConfigureAwait(false);
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

        await PokeLeadAsync(ct).ConfigureAwait(false);

        return commitResult;
    }

    public Task<ReportReadResult> CommitReportBySessionAsync(
        string callerSessionId,
        ReportCommitRequest req,
        CancellationToken ct = default,
        McpExecutionOrigin? origin = null)
    {
        return CommitReportInternalAsync(
            actorKind: null,
            callerSessionId: callerSessionId,
            buildOps: _ => req,
            ct: ct,
            origin: origin);
    }

    private async Task<ReportReadResult> CommitReportInternalAsync(
        EditActorKind? actorKind,
        string? callerSessionId,
        Func<ReportDocument, ReportCommitRequest> buildOps,
        CancellationToken ct,
        WorkerDispatchIntent? dispatch = null,
        McpExecutionOrigin? origin = null)
    {
        var approvalTurn = callerSessionId is not null && _scheduler.TryGetActive(callerSessionId, out var callingTurn)
            ? callingTurn : null;
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            string? authorizedGeneration = null;
            // Observe mailbox admission even after request cancellation so every approval is reclaimed.
            var phase1 = await EnqueueAsync<(ReportReadResult? Completed, IReadOnlyList<PolicyApproval> Asks, ReportCommitRequest EffectiveRequest, HashSet<string> ApprovedKeys)>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                RequirePolicyAvailableCore();

                if (dispatch is not null && await TryReplayWorkerDispatchCoreAsync(dispatch, callerSessionId, ct).ConfigureAwait(false) is { } replay)
                    return (replay, [], null!, []);

                var (editorRole, boundAttemptId) = ResolveEditorIdentityCore(actorKind, callerSessionId);
                var effectiveReq = buildOps(_report);
                TaskBlockSynchronizer.ValidatePreCommitBoundAttemptFence(boundAttemptId, _report, effectiveReq.Ops);
                var preview = _reportEngine.ApplyCommit(_report, effectiveReq.Anchor, effectiveReq.Ops, effectiveReq.Summary);
                TaskBlockSynchronizer.ValidatePostCommitBoundAttemptFence(boundAttemptId, _report, preview);
                authorizedGeneration = _policyGeneration;

                var touchedSections = CollectTouchedSections(_report, preview, effectiveReq.Ops);
                var nextProseChars = preview.NextDocument.Blocks
                    .Where(b => b.Kind == ReportBlockKind.Prose)
                    .Sum(b => b.Markdown?.Length ?? 0);

                ValidateManifestReportRulesCore(editorRole, touchedSections, nextProseChars);
                // Parse and validate the complete prospective task set before any approval is persisted.
                var taskPreview = TaskBlockSynchronizer.SyncTasksFromReport(TrackId, preview.NextDocument, _tasks,
                    _policy?.Manifest.RequireGateForReadWrite ?? false, NowMs());
                RequireAttemptsOutsideVerification(_activeAttempts.Values.Where(attempt =>
                    taskPreview.SupersededOrRemovedActiveTaskIds.Contains(attempt.TaskId)));
                var taskAdmission = await AdmitTaskDeclarationsCoreAsync(preview, ct).ConfigureAwait(false);
                var decisions = taskAdmission.Asks;

                if (_policy is not null)
                {
                    var prevMd = _reportEngine.RenderMarkdown(_report, withMarkers: false);
                    var nextMd = _reportEngine.RenderMarkdown(preview.NextDocument, withMarkers: false);
                    var eval = _policyVm.Before(_policy, "report.commit", TrackId, JsonHelpers.ToElement(new {
                        editorRole, prevMarkdown = prevMd, nextMarkdown = nextMd, touchedSections, nextProseChars
                    }), _track.TemplateInputs, _track.TemplateState);
                    await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);

                    if (eval.Output is GuardDecision.Deny denied) throw KernelException.PolicyDenied(denied.Reason);
                    if (eval.Output is GuardDecision.AskHuman ask) decisions.Add(ask);
                }
                var asks = new List<PolicyApproval>();
                try
                {
                    foreach (var decision in decisions)
                    {
                        RequireApprovalOrigin(callerSessionId, approvalTurn, origin);
                        asks.Add(ApplyGuardDecisionCore(decision, callerSessionId ?? _track.LeadSessionId, approvalTurn, ct)!);
                    }
                }
                catch
                {
                    foreach (var ask in asks) ExpireApproval(ask.Pending);
                    throw;
                }
                if (asks.Count > 0) return (null, asks, effectiveReq, taskAdmission.ApprovedKeys);

                var result = await FinalizeReportCommitCoreAsync(
                    preview,
                    callerSessionId,
                    effectiveReq.CommitMessage ?? $"Report commit ({editorRole})",
                    ct, dispatch, taskAdmission.ApprovedKeys).ConfigureAwait(false);
                return (result, [], effectiveReq, taskAdmission.ApprovedKeys);
            }, CancellationToken.None).ConfigureAwait(false);

            if (phase1.Completed is not null)
            {
                return phase1.Completed;
            }

            try
            {
                var remaining = phase1.Asks.ToList();
                while (remaining.Count > 0)
                {
                    var completed = await Task.WhenAny(remaining.Select(ask => ask.Decision)).WaitAsync(ct).ConfigureAwait(false);
                    var ask = remaining.First(ask => ask.Decision == completed);
                    await RequirePolicyApprovalAsync(ask, ct).ConfigureAwait(false);
                    remaining.Remove(ask);
                }
            }
            finally
            {
                foreach (var ask in phase1.Asks) ExpireApproval(ask.Pending);
            }

            return await EnqueueAsync(async _ =>
            {
                if (dispatch is not null && await TryReplayWorkerDispatchCoreAsync(dispatch, callerSessionId, ct).ConfigureAwait(false) is { } replay)
                    return replay;
                if (_policyGeneration != authorizedGeneration)
                    throw KernelException.PolicyDenied("Active policy changed during task/report approval.");
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
                    ct, dispatch, phase1.ApprovedKeys).ConfigureAwait(false);
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

    private IAgentSession? FindOpenSessionForSettingsCore(string sessionId, string provider) =>
        !_scheduler.HasResumeHold(sessionId) &&
            _openSessionHandles.TryGetValue(sessionId, out var handle) &&
            string.Equals(handle.LaunchSpec.Provider, provider, StringComparison.OrdinalIgnoreCase)
            ? handle.Agent : null;

    private string? ResolveSessionPermissionModeCore(string provider, string? requested, string? sessionId = null) =>
        _options.PermissionModes.Resolve(provider, requested,
            sessionId is null ? null : FindOpenSessionForSettingsCore(sessionId, provider)?.AvailablePermissionModes);

    private async Task<AgentSessionDto> NewSubagentSessionCoreAsync(CreateSessionRequest req, CancellationToken ct)
    {
        var now = NowMs();
        var provider = string.IsNullOrWhiteSpace(req.Provider)
            ? GetDefaultProviderCore()
            : (TaskBlockSynchronizer.NormalizeProviderId(req.Provider) ?? req.Provider!.Trim());
        var permissionMode = ResolveSessionPermissionModeCore(provider, req.PermissionMode);

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
                _scheduler.IsOccupied(existing))
            {
                throw KernelException.Conflict(
                    $"Cannot change provider or model on session '{sessionId}' while a turn is actively running.");
            }

            if (providerChanged || modelChanged)
            {
                _mcpBridge.RevokeSession(sessionId);
                if (_openSessionHandles.Remove(sessionId, out var cachedSession))
                {
                    await cachedSession.Agent.DisposeAsync().ConfigureAwait(false);
                }
            }

            var now = NowMs();
            var nextTitle = req.Title is not null ? req.Title.Trim() : existing.Title;
            var nextArchived = req.Archived.HasValue
                ? (req.Archived.Value ? (existing.ArchivedAtMs ?? now) : null)
                : existing.ArchivedAtMs;
            var nextNativeThreadId = providerChanged ? null : existing.NativeThreadId;
            var nextPermissionMode = providerChanged
                ? ResolveSessionPermissionModeCore(nextProvider, null)
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
            var activeTurn = _scheduler.TryGetActive(sessionId, out var running) ? running.TurnDto : null;
            var unknown = session.Status == "unknown"
                ? await _store.SessionInputs.FindUnresolvedAsync(sessionId, ct).ConfigureAwait(false)
                : null;
            var worker = await ReadWorkerActivityCoreAsync(session, running, activityTurnId, afterSequence, ct).ConfigureAwait(false);
            var queue = await ReadMessageQueueCoreAsync(sessionId, ct).ConfigureAwait(false);
            var settingsSession = session.BoundAttemptId is null && session.Provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase)
                ? FindOpenSessionForSettingsCore(sessionId, session.Provider) : null;
            return new SessionItemsPageDto(items.Select(item => WithMessageStatus(item, running)).ToArray(),
                activeTurn, session, unknown?.Receipt, hasMoreBefore,
                queue.Queue, running?.CanSteerQueuedMessages == true, queue.Handoffs,
                ReadSessionResumeCore(session, running, unknown?.Receipt), worker,
                settingsSession?.AvailablePermissionModes);
        }, ct);
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
            var modelChanged = (session.Model, session.ReasoningEffort) != (model, reasoningEffort);
            var updated = session with
            {
                Model = model, ReasoningEffort = reasoningEffort, UpdatedAtMs = NowMs(),
                ContextUsage = modelChanged ? null : session.ContextUsage
            };
            try
            {
                await PersistAndEmitSessionUpdatedCoreAsync(updated, updated.UpdatedAtMs, ct).ConfigureAwait(false);
            }
            finally
            {
                if (modelChanged && ReferenceEquals(_sessions[sessionId], updated))
                    InvalidateContextBindingCore(sessionId);
            }

            // Never change the provider underneath an active turn. Its next reuse reconciles.
            if (!_scheduler.HasActive(sessionId) && !_scheduler.IsCompacting(sessionId) &&
                !_scheduler.HasResumeHold(sessionId) && _openSessionHandles.ContainsKey(sessionId))
            {
                // Persist even during Drain; the next admitted turn reconciles the cache.
                using var admission = _options.DispatchAdmission.TryEnter();
                if (admission is not null)
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _actorShutdownCts.Token);
                    await GetOrOpenAgentSessionCoreAsync(updated, overrideSystemPrompt: null, linkedCts.Token).ConfigureAwait(false);
                }
            }

            return updated;
        }, ct);
    }

    public async Task InterruptSessionAsync(string sessionId, CancellationToken ct = default, string? turnId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (_scheduler.TryGetActive(sessionId, out var active))
        {
            if (active.Settings is not null)
                throw KernelException.InvalidArgument("Worker interruption requires attemptId and turnId.");
            if (turnId is not null && active.TurnDto.Id != turnId)
                throw KernelException.Conflict("The observed provider turn is no longer current.");
            await RequestTurnStop(active).WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            if (session.BoundAttemptId is not null)
                throw KernelException.InvalidArgument("Worker interruption requires attemptId and turnId.");
            if (session.Status == "queued" || turnId is not null)
                throw KernelException.Conflict("The observed provider turn is no longer current.");
        }, ct).ConfigureAwait(false);
    }

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
        return await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            if (session.BoundTaskId is not null)
            {
                throw KernelException.InvalidArgument("Cannot change permission mode on a task-bound session.");
            }

            if (string.IsNullOrWhiteSpace(permissionMode))
                throw KernelException.InvalidArgument("Permission mode is required.");
            var trimmed = ResolveSessionPermissionModeCore(session.Provider, permissionMode, sessionId);
            if (string.Equals(session.PermissionMode, trimmed, StringComparison.Ordinal))
            {
                return session;
            }

            var idle = !_scheduler.HasActive(sessionId) && !_scheduler.IsCompacting(sessionId) && !_scheduler.HasResumeHold(sessionId);
            _openSessionHandles.TryGetValue(sessionId, out var cached);
            var next = session with
            {
                NativeThreadId = session.NativeThreadId ?? (idle && cached?.Agent.ConfigurationState is not null ? cached.Agent.NativeThreadId : null),
                PermissionMode = trimmed,
                UpdatedAtMs = NowMs()
            };
            await PersistAndEmitSessionUpdatedCoreAsync(next, next.UpdatedAtMs, ct).ConfigureAwait(false);
            _sessions[sessionId] = next;

            return next;
        }, ct).ConfigureAwait(false);
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

            if (task.LatestAttemptId is not null &&
                _activeAttempts.TryGetValue(task.LatestAttemptId, out var activeAttempt))
            {
                settledAttempt = await CancelAndDiscardActiveAttemptCoreAsync(activeAttempt, reason ?? "Canceled by governance.", now, ct)
                    .ConfigureAwait(false);
            }

            var updatedTask = task with
            {
                Status = TaskRunStatus.Canceled,
                Ready = false,
                StatusDetail = string.IsNullOrWhiteSpace(reason) ? "Canceled." : reason!.Trim(),
                UpdatedAtMs = now
            };
            ReplaceTaskInMemoryCore(updatedTask);

            var after = EvaluateAfterPolicyEventCore("task.settled", JsonHelpers.ToElement(new {
                taskKey = task.TaskKey, accessMode = FormatAccessModeForPolicy(task.Access),
                attemptStatus = "canceled", workerResult = settledAttempt?.WorkerResult,
                meta = settledAttempt?.Meta ?? JsonHelpers.EmptyObject, gateVerdict = (JsonElement?)null
            }));
            await PersistAndEmitTaskUpdatedCoreAsync(updatedTask, settledAttempt, after.NextState, now, ct, after.Output).ConfigureAwait(false);

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

    // ------------------- Asks, Mail, Terminals, Policy Hooks & Schedules -------------------

    public async Task<JsonElement> AskUserAsync(
        string? callerSessionId,
        IReadOnlyList<AskQuestionDto> questions,
        CancellationToken ct = default)
    {
        if (questions.Count == 0)
        {
            throw KernelException.InvalidArgument("At least one question is required for perga_user_ask.");
        }

        if (questions.Any(question => question.IsMultiSelect))
            throw KernelException.InvalidArgument("perga_user_ask does not support isMultiSelect: true.");

        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var waiter = await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                if (callerSessionId is not null)
                {
                    RequireSessionCore(callerSessionId);
                }

                return await CreateWakeAskCoreAsync(questions, callerSessionId, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);


            return await waiter.WaitAsync(ct).ConfigureAwait(false);
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

            if (existingAsk.Delivery != AskDelivery.Wake)
                throw KernelException.Conflict("This approval request is no longer pending.");

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

            var shouldWakeLead = _track.ClosedAtMs is null && !string.IsNullOrEmpty(_track.LeadSessionId);
            return (answered, shouldWakeLead);
        }, ct).ConfigureAwait(false);

        if (outcome.ShouldWakeLead)
        {
            await PokeLeadAsync(ct).ConfigureAwait(false);
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
            await PokeLeadAsync(_actorShutdownCts.Token).ConfigureAwait(false);
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

    internal string? ReadActiveTurnId(string sessionId) =>
        _scheduler.TryGetActive(sessionId, out var active) ? active.TurnDto.Id : null;

    public async Task<string> GuardMcpToolCallAsync(
        string sessionId,
        string mcpServer,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default,
        McpExecutionOrigin? origin = null)
    {
        var approvalTurn = _scheduler.TryGetActive(sessionId, out var callingTurn) ? callingTurn : null;
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            string authorizedPolicyGeneration = null!;
            var askSuspension = await EnqueueAsync<PolicyApproval?>(async _ =>
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

                var previewOnly = false;
                if (mcpServer == "perga" && toolName == KernelMcpToolNames.TrackSet)
                    previewOnly = ValidateTemplateSetArguments(arguments);
                if (!(mcpServer == "perga" && toolName == KernelMcpToolNames.TrackSet))
                    RequirePolicyAvailableCore();
                if (_policy is null)
                {
                    return null;
                }

                var eval = _policyVm.Before(_policy, "tool." + toolName, TrackId, JsonHelpers.ToElement(new {
                    sessionId = caller.Id, isLead = caller.IsLead, boundAttemptId = caller.BoundAttemptId,
                    mcpServer, toolName, arguments = arguments.ValueKind == JsonValueKind.Undefined ? JsonHelpers.EmptyObject : arguments
                }), _track.TemplateInputs, _track.TemplateState);
                if (eval.Output is GuardDecision.AskHuman)
                    RequireApprovalOrigin(sessionId, approvalTurn, origin);
                // An allowed replacement initializes its own state atomically. Denied or
                // suspended attempts still retain the current policy's guard state updates.
                if (!previewOnly && (!string.Equals(mcpServer, "perga", StringComparison.OrdinalIgnoreCase) ||
                    toolName != KernelMcpToolNames.TrackSet || eval.Output is not GuardDecision.Allow))
                    await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);

                if (previewOnly && eval.Output is GuardDecision.AskHuman)
                    throw KernelException.PolicyDenied("Template preview requires human approval; dryRun cannot create an approval request.");

                return ApplyGuardDecisionCore(eval.Output, caller.Id, approvalTurn, ct);
            }, ct).ConfigureAwait(false);

            if (askSuspension is null)
            {
                return authorizedPolicyGeneration;
            }


            await RequirePolicyApprovalAsync(askSuspension, ct).ConfigureAwait(false);
            return authorizedPolicyGeneration;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
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

        try
        {
            if (!string.IsNullOrWhiteSpace(snapshot.TemplateSnapshotSource))
                _policy = _templateCatalog.GetOrCompileSnapshot(snapshot.TemplateSnapshotSource!);
            else if (!string.IsNullOrWhiteSpace(_track.TemplateId))
                throw new InvalidOperationException("Active template has no frozen snapshot.");
        }
        catch (Exception ex)
        {
            _policy = null;
            _policyUnavailableReason = $"Active template is unavailable: {ex.Message}";
        }

        _policyGeneration = Guid.NewGuid().ToString("N");

        SetApprovalAdmissionClosed(_track.ClosedAtMs is not null);
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
        WorkerDispatchIntent? dispatch = null, IReadOnlySet<string>? approvedKeys = null)
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
        foreach (var admitted in _tasks.Where(t => approvedKeys?.Contains(t.TaskKey) == true))
            _taskDispatchApprovals[admitted.TaskKey] = (admitted.Generation, _policyGeneration, TaskDispatchFields(admitted));
        var committedCt = _actorShutdownCts.Token;
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, reportEvt, committedCt).ConfigureAwait(false);

        var taskEvt = CreateTaskUpdatedEventCore(now);
        await _store.AppendEventAsync(taskEvt, committedCt).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, taskEvt, committedCt).ConfigureAwait(false);

        await AfterPolicyEventCoreAsync("report.commit", reportPayload, committedCt).ConfigureAwait(false);
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
        if (_policyUnavailableReason is not null || _options.DispatchAdmission.Draining || _deletingTrack || _track.ClosedAtMs is not null || !WorkspaceReady)
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
                PolicyTaskRoute route;
                try
                {
                    route = RouteTaskCore(task);
                    if (!await EnsureTaskDispatchApprovedCoreAsync(task, route, ct).ConfigureAwait(false)) continue;
                    await ValidateTaskProviderModelCoreAsync(route.Provider, route.Model, ct).ConfigureAwait(false);
                }
                catch (KernelException ex) when (ex.Code == KernelErrorCode.Conflict && !ct.IsCancellationRequested)
                {
                    // Probe outages are retryable; retain the pending task for the next scheduling pass.
                    continue;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var failed = task with { Status = TaskRunStatus.Failed, StatusDetail = ex.Message, UpdatedAtMs = NowMs() };
                    await PersistAndEmitTaskUpdatedCoreAsync(failed, null, null, failed.UpdatedAtMs, ct).ConfigureAwait(false);
                    ReplaceTaskInMemoryCore(failed);
                    continue;
                }
                if (!await StartTaskAttemptCoreAsync(task, route, driver, mounted, ct).ConfigureAwait(false))
                    preparationFailed = true;
            }
        } while (preparationFailed);
    }

    private async Task<bool> StartTaskAttemptCoreAsync(
        TaskDto task,
        PolicyTaskRoute route,
        IWorkspaceDriver driver,
        MountedWorkspace mounted,
        CancellationToken ct)
    {
        var admission = _options.DispatchAdmission.TryEnter();
        if (admission is null) return true;
        var transferred = false;

        try
        {
            var now = NowMs();
            var attemptId = $"att_{Guid.NewGuid():N}";
            var isCommandTask = string.Equals(task.Kind, "command", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(task.Kind, "cmd", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(task.Kind, "shell", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(task.Kind, "terminal", StringComparison.OrdinalIgnoreCase);
            var workerProvider = route.Provider ?? GetDefaultProviderCore();
            var subagent = isCommandTask ? null : new AgentSessionDto(
                Id: $"ses_{Guid.NewGuid():N}",
                TrackId: TrackId,
                IsLead: false,
                BoundTaskId: task.Id,
                BoundAttemptId: attemptId,
                Title: $"Task: {task.TaskKey}",
                Provider: workerProvider,
                Model: route.Model ?? ResolveTaskWorkerModelCore(workerProvider),
                NativeThreadId: null,
                Epoch: 1,
                Status: "idle",
                ArchivedAtMs: null,
                CreatedAtMs: now,
                UpdatedAtMs: now,
                PermissionMode: _options.PermissionModes.Resolve(workerProvider, null));

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
                _ = TrackBackgroundJob(async () =>
                {
                    using (admission) await RunCommandTaskAttemptInBackgroundAsync(attemptId, runningTask, lease, attemptToken).ConfigureAwait(false);
                });
            else
            {
                var prompt = BuildContractSubagentPrompt(runningTask);
                var startupReservation = Guid.NewGuid();
                _scheduler.Reserve(subagent.Id, startupReservation);
                _ = TrackBackgroundJob(async () =>
                {
                    using (admission) await RunSubagentTaskAttemptInBackgroundAsync(attemptId, subagent.Id, prompt, startupReservation, attemptToken).ConfigureAwait(false);
                });
            }

            transferred = true;
            var evt = sessionEvent ?? CreateTaskUpdatedEventCore(now);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        finally { if (!transferred) admission.Dispose(); }
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
            await _turnRunner.RunAsync(new ContractWorkerLaunchWorkItem(this, subagentSessionId, prompt, reservation, attemptId), ct, alreadyAdmitted: true).ConfigureAwait(false);

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
                    if (_scheduler.TryGetReservation(subagentSessionId, out var held) && held == reservation)
                        _scheduler.ReleaseReservation(subagentSessionId);
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

                var after = EvaluateAfterPolicyEventCore("task.settled", JsonHelpers.ToElement(new {
                    taskKey = currentTask.TaskKey, accessMode = FormatAccessModeForPolicy(currentTask.Access),
                    attemptStatus = settledAttempt.Status, workerResult = settledAttempt.WorkerResult,
                    meta = mergedMeta, gateVerdict
                }));
                var nextTemplateState = after.NextState;
                var policyNote = after.Output;

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

                await PersistAndEmitTaskUpdatedCoreAsync(settledTask, settledAttempt, nextTemplateState, now, verificationToken, summarySb.ToString())
                    .ConfigureAwait(false);

                await ScheduleEligibleTasksCoreAsync(verificationToken).ConfigureAwait(false);

                return (settledTask, summarySb.ToString());
            }, verificationToken).ConfigureAwait(false);

            await PokeLeadAsync(verificationToken).ConfigureAwait(false);
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

                var after = EvaluateAfterPolicyEventCore("task.settled", JsonHelpers.ToElement(new {
                    taskKey = currentTask.TaskKey, accessMode = FormatAccessModeForPolicy(currentTask.Access),
                    attemptStatus = "failed", workerResult = clonedWorkerResult,
                    meta = active.AttemptDto.Meta, gateVerdict = (JsonElement?)null
                }));
                var nextTemplateState = after.NextState;
                var policyNote = after.Output;

                var msg = string.IsNullOrWhiteSpace(policyNote)
                    ? $"Task '{updatedTask.TaskKey}' (attempt '{attemptId}') failed: {reason}"
                    : $"Task '{updatedTask.TaskKey}' (attempt '{attemptId}') failed: {reason}\n{policyNote}";
                await PersistAndEmitTaskUpdatedCoreAsync(updatedTask, updatedAttempt, nextTemplateState, now, ct, msg)
                    .ConfigureAwait(false);
                await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);

                return (updatedTask, msg);
            }, ct).ConfigureAwait(false);

            await PokeLeadAsync(ct).ConfigureAwait(false);
            return failedTask;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private void RequireDispatchWorkspace(AgentSessionDto session)
    {
        if ((session.BoundTaskId is not null || session.BoundAttemptId is not null) &&
            (session.BoundAttemptId is not { } attemptId || !_activeAttempts.ContainsKey(attemptId)))
            throw KernelException.Conflict("The task attempt workspace is no longer available.");
    }

    private static SessionItemDto CreateWakeUserItem(SessionTurnDto turn, string prompt, string? inputId, string? deliveryError = null, string? presentation = null) =>
        new(Id: 0, SessionId: turn.SessionId, TurnId: turn.Id, Kind: "user",
            Payload: inputId is null
                ? JsonHelpers.ToElement(new { v = 1, content = prompt, triggerKind = turn.TriggerKind, presentation })
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

    private async Task<AgentSessionDto> CompactSessionInternalAsync(string sessionId, CancellationToken ct, bool automatic = false)
    {
        using var dispatch = automatic ? null : await _options.DispatchAdmission.EnterAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _inFlightExternalOps);
        var reserved = false;
        try
        {
            var preparation = await EnqueueAsync<(IAgentSession OldAgentSession, LiveContextUsage ContextBinding, int HandoverTurnIndex)?>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var session = RequireSessionCore(sessionId);
                if (session.BoundAttemptId is not null)
                    throw KernelException.Conflict("Worker sessions do not support a manual handover turn.");
                RequireSessionDispatchAvailableCore(sessionId);
                if (automatic && !(CurrentContextUsageRatioCore(session) >= _options.AutoCompactionThreshold))
                    return null;
                if (_scheduler.IsOccupied(session, allowFinishingDispatch: true))
                {
                    throw KernelException.Conflict($"Cannot compact session '{sessionId}' while a turn is actively running.");
                }

                var oldAgentSession = await GetOrOpenAgentSessionCoreAsync(session, overrideSystemPrompt: null, ct)
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                _scheduler.StartCompacting(sessionId);
                reserved = true;
                var turnIdx = NextTurnIndexCore(sessionId);
                return (oldAgentSession, _liveContextUsage.GetOrCreateValue(oldAgentSession), turnIdx);
            }, CancellationToken.None).ConfigureAwait(false);

            if (preparation is not { } prep)
                return await EnqueueAsync(_ => Task.FromResult(RequireSessionCore(sessionId)), ct).ConfigureAwait(false);

            if (await prep.OldAgentSession.ResolveNativeCompactSupportAsync(ct).ConfigureAwait(false))
            {
                var compactTurnId = $"trn_{prep.HandoverTurnIndex:D6}_{Guid.NewGuid():N}";
                var nativeStartedAt = NowMs();
                var nativeResult = await prep.OldAgentSession.CompactAsync(ct).ConfigureAwait(false);
                var compactSummary = string.IsNullOrWhiteSpace(nativeResult.Summary)
                    ? "Context compacted."
                    : nativeResult.Summary.Trim();

                return await EnqueueAsync(async _ =>
                {
                    var current = RequireSessionCore(sessionId);
                    var now = NowMs();

                    var compactTurn = new SessionTurnDto(
                        Id: compactTurnId,
                        SessionId: sessionId,
                        TurnIndex: prep.HandoverTurnIndex,
                        Epoch: current.Epoch,
                        TriggerKind: "system",
                        Input: JsonHelpers.ToElement(new { prompt = "/compact", kind = "native_compact" }),
                        Status: "completed",
                        Usage: nativeResult.Usage is null ? null : JsonHelpers.ToElement(nativeResult.Usage),
                        ErrorText: null,
                        StartedAtMs: nativeStartedAt,
                        CompletedAtMs: now);

                    var compactItems = new List<SessionItemDto>
                    {
                        new(0, sessionId, compactTurnId, "user", JsonHelpers.ToElement(new { v = 1, content = "/compact" }), nativeStartedAt),
                        new(0, sessionId, compactTurnId, "assistant_text", JsonHelpers.ToElement(new { v = 1, text = compactSummary }), now)
                    };

                    var compacted = current;
                    if (IsCurrentContextBindingCore(current, prep.OldAgentSession, prep.ContextBinding))
                    {
                        compacted = nativeResult.Usage is { } usage
                            ? ApplyContextUsageCore(current, prep.OldAgentSession, prep.ContextBinding, nativeResult.ContextUsageRevision, usage, now)
                            : current with { ContextUsage = null };
                        compacted = ApplyTrailingContextUsageCore(compacted, prep.OldAgentSession, prep.ContextBinding, nativeResult.CompletedPromptId, now);
                    }
                    compacted = compacted with
                    {
                        NativeThreadId = current.NativeThreadId ?? prep.OldAgentSession.NativeThreadId,
                        Status = "idle",
                        UpdatedAtMs = now
                    };
                    _sessions[sessionId] = compacted;

                    await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
                    await _store.RecordTurnAndItemsAsync(compactTurn, compactItems, compacted, ct).ConfigureAwait(false);
                    await PersistAndEmitSessionUpdatedCoreAsync(compacted, now, ct).ConfigureAwait(false);

                    return compacted;
                }, ct).ConfigureAwait(false);
            }

            // Fallback: Request structured handover summary turn on current IAgentSession and bump Epoch.
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

            // 3 & 4. Update Epoch = Epoch + 1, NativeThreadId = null, clear context usage -> seed fresh IAgentSession with latest Report + handover summary!
            return await EnqueueAsync(async _ =>
            {
                _openSessionHandles.Remove(sessionId);

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
                    ContextUsage = null,
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
                var fresh = await OpenFreshAgentSessionCoreAsync(compacted, seedPrompt, ct).ConfigureAwait(false);
                _scheduler.EndCompacting(sessionId);
                await RegisterOpenAgentSessionCoreAsync(compacted, fresh.Agent, fresh.Spec, ct).ConfigureAwait(false);

                return RequireSessionCore(sessionId);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            if (reserved && !_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(async _ =>
                {
                    _scheduler.EndCompacting(sessionId);
                    await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private async Task<IAgentSession> GetOrOpenAgentSessionCoreAsync(
        AgentSessionDto session,
        string? overrideSystemPrompt,
        CancellationToken ct)
    {
        var desired = SessionConfiguration.FromNullable(session.Model, session.ReasoningEffort, session.PermissionMode);
        if (_openSessionHandles.TryGetValue(session.Id, out var handle))
        {
            // Capture confirmed identity before configuration can partially mutate the provider
            // or throw. Compaction may have registered it before any prompt persisted it.
            if (handle.Agent.ConfigurationState is not null && handle.Agent.NativeThreadId is { } confirmedNativeId)
            {
                var current = RequireSessionCore(session.Id);
                if (current.NativeThreadId is null)
                {
                    var identified = current with { NativeThreadId = confirmedNativeId, UpdatedAtMs = NowMs() };
                    await PersistAndEmitSessionUpdatedCoreAsync(identified, identified.UpdatedAtMs, ct).ConfigureAwait(false);
                }
                session = session with { NativeThreadId = current.NativeThreadId ?? confirmedNativeId };
            }
            SessionConfigurationOutcome outcome;
            try
            {
                outcome = await handle.Agent.ConfigureAsync(desired, ct).ConfigureAwait(false);
                if (outcome is SessionConfigurationOutcome.Applied applied)
                {
                    RequireConfirmedConfiguration(session, handle.Agent, applied.Configuration);
                    if (handle.Confirmation != applied.Configuration)
                        InvalidateContextBindingCore(session.Id);
                    _openSessionHandles[session.Id] = handle with { Selection = applied.Configuration.Selection, Confirmation = applied.Configuration };
                    return handle.Agent;
                }
            }
            catch
            {
                _openSessionHandles.Remove(session.Id);
                InvalidateContextBindingCore(session.Id);
                handle.Agent.AttachIdleContextUsageHandler(null);
                await handle.Agent.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _openSessionHandles.Remove(session.Id);
            InvalidateContextBindingCore(session.Id);
            handle.Agent.AttachIdleContextUsageHandler(null);
            await handle.Agent.DisposeAsync().ConfigureAwait(false);
            switch (outcome)
            {
                case SessionConfigurationOutcome.RequiresReopen:
                    break;
                case SessionConfigurationOutcome.Unsupported unsupported:
                    throw KernelException.InvalidArgument(unsupported.Reason);
                case SessionConfigurationOutcome.Rejected rejected:
                    throw KernelException.PreconditionFailed(rejected.Reason);
                default:
                    throw KernelException.PreconditionFailed("The provider returned an unknown configuration outcome.");
            }
        }

        var opened = await OpenFreshAgentSessionCoreAsync(session, overrideSystemPrompt, ct).ConfigureAwait(false);
        await RegisterOpenAgentSessionCoreAsync(session, opened.Agent, opened.Spec, ct).ConfigureAwait(false);
        return opened.Agent;
    }

    private async Task<(IAgentSession Agent, HarnessLaunchSpec Spec)> OpenFreshAgentSessionCoreAsync(
        AgentSessionDto session,
        string? overrideSystemPrompt,
        CancellationToken ct)
    {
        var spec = await BuildAgentLaunchSpecCoreAsync(session, overrideSystemPrompt, ct).ConfigureAwait(false);
        IAgentSession opened;
        try { opened = await _agentRuntime.OpenSessionAsync(spec, ct).ConfigureAwait(false); }
        catch (SessionConfigurationException error)
        { throw KernelException.PreconditionFailed(error.Message); }
        try
        {
            if (spec.ResumeNativeThreadId is not null && opened.NativeThreadId != spec.ResumeNativeThreadId)
                throw KernelException.PreconditionFailed("The provider did not restore the original session.");
            RequireConfirmedConfiguration(session, opened, opened.ConfigurationState);
            return (opened, spec);
        }
        catch
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void RequireConfirmedConfiguration(AgentSessionDto session, IAgentSession agent, ConfirmedConfiguration? confirmed)
    {
        // Workers have a separate, versioned per-turn selection/readback contract.
        if (session.BoundAttemptId is not null) return;
        var requested = SessionConfiguration.FromNullable(session.Model, session.ReasoningEffort, session.PermissionMode);
        if (confirmed?.Selection != requested || confirmed != agent.ConfigurationState ||
            confirmed?.ContextUsageRevision is { } revision && revision != agent.ContextUsageRevision)
            throw KernelException.PreconditionFailed("The provider did not confirm the complete session configuration.");
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
        await _scheduler.PokeLeadAsync().ConfigureAwait(false);
    }

    private void RequirePolicyAvailableCore()
    {
        if (_policyUnavailableReason is { } reason) throw KernelException.PolicyDenied(reason);
    }

    private async Task<Task<JsonElement>> CreateWakeAskCoreAsync(
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
            AnsweredAtMs: null,
            Delivery: AskDelivery.Wake,
            SessionId: callerSessionId,
            TurnId: callerSessionId is not null && _scheduler.TryGetActive(callerSessionId, out var owner) ? owner.TurnDto.Id : null);

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
        _asks[ask.Id] = ask;
        _pendingAskWaiters[ask.Id] = tcs;
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
        // Request cancellation expires approval authority even if the ask stays visible.
        if (ct.CanBeCanceled)
            _ = ct.Register(() => tcs.TrySetCanceled(ct));

        return tcs.Task;
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

        if (active.SessionId is not null && _scheduler.TryGetActive(active.SessionId, out var runningTurn))
        {
            AbortTurn(runningTurn);
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
                    string.Equals(s.H1.Trim().TrimStart('#').Trim(), touched.Trim().TrimStart('#').Trim(), StringComparison.OrdinalIgnoreCase));

                if (rule?.EditableBy is { } allowed &&
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

    private async Task UpdateTemplateStateIfChangedCoreAsync(JsonElement nextState, CancellationToken ct, bool emitEvent = false)
    {
        if (nextState.ValueKind == JsonValueKind.Undefined || JsonElement.DeepEquals(_track.TemplateState, nextState))
        {
            return;
        }

        var nextTrack = _track with { TemplateState = nextState.Clone(), UpdatedAtMs = NowMs() };
        var evt = emitEvent ? new EventEnvelopeDto(null, nextTrack.AreaId, TrackId, KernelEventKinds.TrackUpdated,
            JsonHelpers.ToElement(TemplatePresentation.Project(nextTrack, _policy, _policyVm)), nextTrack.UpdatedAtMs) : null;
        if (evt is not null) await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpdateTrackAndEmitAsync(nextTrack, evt, ct).ConfigureAwait(false);
        _track = nextTrack;
        if (evt is not null) await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EmitTrackUpdatedCoreAsync(long now, CancellationToken ct)
    {
        var evt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.TrackUpdated,
            Payload: JsonHelpers.ToElement(PresentTrackCore()),
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
        CancellationToken ct, string? wakePrompt = null)
    {
        var evt = CreateTaskUpdatedEventCore(now);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpsertTaskAndAttemptAsync(task, attempt, nextTemplateState, evt, ct, wakePrompt: wakePrompt).ConfigureAwait(false);
        if (nextTemplateState is { } state) _track = _track with { TemplateState = state };
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

    private TrackDto PresentTrackCore() => TemplatePresentation.Project(_track, _policy, _policyVm);

    private TrackAggregateSnapshot BuildSnapshotCore()
    {
        return new TrackAggregateSnapshot(
            Track: PresentTrackCore(),
            Report: _report,
            Tasks: _tasks.ToArray(),
            Sessions: OrderedSessionsCore(),
            OpenAsks: _asks.Values.Where(a => a.Status == AskStatus.Open).OrderBy(a => a.CreatedAtMs).ToArray(),
            TemplateSnapshotSource: _templateSnapshotSource,
            WorkspacePreparation: WorkspacePreparationStatus) { PendingApprovals = ReadPendingApprovals() };
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

        var templateNote = session.IsLead
            ? "\n\nTemplates are optional and implicit. If useful, discover them with perga_template_ls, inspect a template or the authoring contract with perga_template_cat, inspect the active snapshot with perga_template_cat(__current__), apply or preview with perga_track_set (templateId or JSON/.perga.js source, inputs, dryRun), detach with clear, and update inputs with perga_template_update_inputs (preserves state)."
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
        SetApprovalAdmissionClosed(true);

        try
        {
            await _mailboxLoopTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown cancellation.
        }

        foreach (var kvp in _openSessionHandles.ToArray())
        {
            _openSessionHandles.Remove(kvp.Key);

            try
            {
                await kvp.Value.Agent.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort session disposal on eviction/shutdown.
            }
        }

        _actorShutdownCts.Dispose();
    }

    private sealed class ActiveTurnState(
        string sessionId,
        SessionConfiguration executionConfiguration,
        LiveContextUsage contextBinding,
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
        public ExecutionControl Control { get; } = new(starting: true);
        public TurnActivity Activity { get; } = new();
        public WorkerSettings? Settings { get; init; }
        public LiveContextUsage ContextBinding { get; } = contextBinding;
        // Frozen inputs can execute a configuration different from the committed selection.
        public bool MatchesSessionConfiguration(AgentSessionDto session) =>
            executionConfiguration == SessionConfiguration.FromNullable(session.Model, session.ReasoningEffort, session.PermissionMode);
        public bool Interrupted
        {
            get { lock (Control.Sync) return Control.StopRequested; }
        }
        public void CancelStream()
        {
            try { TurnCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
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


}
