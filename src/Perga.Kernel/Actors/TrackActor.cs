using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Flow;
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
    private readonly IFlowTrackSpawner _flowTrackSpawner;
    private readonly IReportDocEngine _reportEngine;
    private readonly IPolicyVm _policyVm;
    private readonly ITemplateCatalog _templateCatalog;
    private readonly IExecutionEnvironmentRegistry _executionEnvironments;
    private readonly IMcpSessionBridge _mcpBridge;
    private readonly EventHub _eventHub;
    private readonly KernelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IScheduleCronEvaluator? _scheduleCronEvaluator;

    private IExecutionEnvironment Env => _executionEnvironments.GetRequired(_track.ExecutionTargetId);
    private IAgentRuntime EffectiveAgentRuntime => _track is null ? _executionEnvironments.Default.AgentRuntime : Env.AgentRuntime;
    private IWorkspaceDriverRegistry EffectiveWorkspaceDrivers => Env.WorkspaceDrivers;
    private ITerminalBridgeClient EffectiveTerminalClient => Env.Terminals;
    private IShellCommandRunner EffectiveCommandRunner => Env.Shell;

    private readonly Channel<Func<TrackActor, Task>> _mailbox;
    private readonly CancellationTokenSource _actorShutdownCts = new();
    private readonly Task _mailboxLoopTask;

    // Actor-owned mutable state (mutated strictly on the single-writer mailbox loop)
    private bool _hydrated;
    private volatile bool _closingTrack;
    private volatile TrackDto _track = null!;
    private volatile string? _executionTargetId;
    private CompiledPolicy? _policy;
    private string? _policyUnavailableReason;
    private volatile string _policyGeneration = Guid.NewGuid().ToString("N");
    private string? _templateSnapshotSource;
    private MountedWorkspace? _mountedWorkspace;
    private readonly Dictionary<string, AgentSessionDto> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextTurnIndexBySession = new(StringComparer.Ordinal);
    private readonly SessionRuntimeRegistry _runtimes;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private readonly TerminalLifecycle _terminalLifecycle;
    private readonly TrackInteractions _interactions;
    private readonly TrackReport _reports;
    private readonly TrackTasks _taskCoordinator;
    private readonly SessionTurnScheduler _scheduler;
    private readonly SessionTurnRunner _turnRunner;
    private readonly ConcurrentDictionary<Guid, Task> _backgroundJobs = new();

    private int _inFlightExternalOps;
    private int _queuedMailboxOps;
    private long _lastActivityAtMs;
    private volatile bool _disposed;

    internal TrackActor(
        string trackId,
        ITrackStore store,
        IFlowTrackSpawner flowTrackSpawner,
        IReportDocEngine reportEngine,
        IPolicyVm policyVm,
        ITemplateCatalog templateCatalog,
        IExecutionEnvironmentRegistry executionEnvironments,
        IMcpSessionBridge mcpBridge,
        EventHub eventHub,
        KernelOptions options,
        TimeProvider timeProvider,
        IScheduleCronEvaluator? scheduleCronEvaluator,
        IMemoryContextProvider? memoryContext = null)
    {
        TrackId = trackId;
        _terminalLifecycle = new TerminalLifecycle(this);
        _interactions = new TrackInteractions(this);
        _reports = new TrackReport(this);
        _taskCoordinator = new TrackTasks(this);
        _scheduler = new SessionTurnScheduler(this);
        _turnRunner = new SessionTurnRunner(this);
        _store = store;
        _flowTrackSpawner = flowTrackSpawner;
        _scheduleCronEvaluator = scheduleCronEvaluator;
        _memoryContext = memoryContext ?? NoMemoryContextProvider.Instance;
        _flowFileStore = new TrackFlowFileStore(options);
        _reportEngine = reportEngine;
        _policyVm = policyVm;
        _templateCatalog = templateCatalog;
        _executionEnvironments = executionEnvironments;
        _mcpBridge = mcpBridge;
        _eventHub = eventHub;
        _options = options;
        _timeProvider = timeProvider;
        _runtimes = new(timeProvider, PrepareRuntimeCloseAsync, (id, binding) => _mcpBridge.RevokeSession(id, binding));
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
        _taskCoordinator.HasActiveWork ||
        _activeWatches.Count > 0 ||
        Volatile.Read(ref _queuedMailboxOps) > 0 ||
        Volatile.Read(ref _inFlightExternalOps) > 0 ||
        !_backgroundJobs.IsEmpty ||
        _scheduler.HasActiveWork;

    internal bool CanEvictIdleActor =>
        !HasActiveWork && (_options.SessionIdleTimeout <= TimeSpan.Zero || !_runtimes.HasOpenHandles);

    public bool IsSessionOrTaskRunning =>
        _scheduler.HasActiveTurns || _taskCoordinator.HasActiveWork || _activeWatches.Count > 0 || _scheduler.HasDispatchingInputs;

    public async Task<T> EnqueueAsync<T>(Func<TrackActor, Task<T>> work, CancellationToken ct = default, bool touchActivity = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (touchActivity) TouchActivity();
        Interlocked.Increment(ref _queuedMailboxOps);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = _mailbox.Writer.TryWrite(async actor =>
        {
            try
            {
                if (touchActivity) TouchActivity();
                var result = await work(actor).ConfigureAwait(false);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (touchActivity) TouchActivity();
                Interlocked.Decrement(ref _queuedMailboxOps);
            }
        });

        if (!written)
        {
            Interlocked.Decrement(ref _queuedMailboxOps);
            ObjectDisposedException.ThrowIf(true, this);
        }

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public Task EnqueueAsync(Func<TrackActor, Task> work, CancellationToken ct = default, bool touchActivity = true)
    {
        return EnqueueAsync<bool>(async actor =>
        {
            await work(actor).ConfigureAwait(false);
            return true;
        }, ct, touchActivity);
    }

    // ------------------- Public Actor Operations -------------------

    public Task<TrackAggregateSnapshot> GetAggregateSnapshotAsync(CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await RefreshForeignWorkspacePreparationCoreAsync(ct).ConfigureAwait(false);
            StartWorkspacePreparationCore(retry: false);
            var track = await ProjectMemoryDataCoreAsync(ct).ConfigureAwait(false);
            return BuildSnapshotCore(track);
        }, ct);
    }

    public async Task<string> GetExecutionTargetIdAsync(CancellationToken ct = default)
    {
        if (_executionTargetId is { } targetId) return targetId;
        var track = await _store.GetTrackAsync(TrackId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Track '{TrackId}' not found.");
        return _executionTargetId = track.ExecutionTargetId;
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
                Track: TemplatePresentation.Project(await ProjectMemoryDataCoreAsync(ct).ConfigureAwait(false), _policy, _policyVm),
                Sessions: OrderedSessionsCore(),
                Tasks: _taskCoordinator.Snapshot(),
                OpenAsks: _interactions.OpenAsks(),
                Terminals: _terminalLifecycle.Snapshot(),
                UnreadMailCount: unreadMails.Count) { PendingApprovals = ReadPendingApprovals() };
        }, ct);
    }

    private async Task<TResult> RunWithMailboxReservationAsync<TReservation, TResult>(
        Func<Task<TReservation>> reserve, Func<TReservation, Task<TResult>> run, Func<TReservation, Task> release)
    {
        var reservation = await EnqueueAsync(_ => reserve(), CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await run(reservation).ConfigureAwait(false);
        }
        finally
        {
            await EnqueueAsync(_ => release(reservation), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task<ReportReadResult> ReadReportAsync(string? callerSessionId = null, CancellationToken ct = default)
        => _reports.ReadReportAsync(callerSessionId, ct);

    public Task<ReportReadResult> CommitReportBySessionAsync(
        string callerSessionId,
        ReportCommitRequest request,
        CancellationToken ct = default,
        InteropPrincipal.Session? authorization = null)
        => _reports.CommitReportBySessionAsync(callerSessionId, request, ct, authorization);

    internal Task RefreshTaskDiagnosticsAsync(CancellationToken ct)
        => _taskCoordinator.RefreshTaskDiagnosticsAsync(ct);

    public Task<IReadOnlyList<TaskDto>> ListTasksAsync(CancellationToken ct = default)
        => _taskCoordinator.ListTasksAsync(ct);

    public Task<TaskDto> CancelTaskAsync(string? callerSessionId, string taskKeyOrId, string? reason = null, CancellationToken ct = default, InteropPrincipal.Session? authorization = null)
        => _taskCoordinator.CancelTaskAsync(callerSessionId, taskKeyOrId, reason, ct, authorization);

    public Task<TaskDto> CompleteTaskDoneAsync(
        string callerSessionId,
        JsonElement? workerResult = null,
        CancellationToken ct = default,
        WorkerCompletionOrigin? origin = null,
        FlowOutcome outcome = FlowOutcome.Ok,
        string? summary = null)
        => _taskCoordinator.CompleteTaskDoneAsync(callerSessionId, workerResult, ct, origin, outcome, summary);

    public Task<TaskDto> CompleteTaskFailAsync(
        string callerSessionId,
        string reason,
        JsonElement? workerResult = null,
        CancellationToken ct = default, WorkerCompletionOrigin? origin = null)
        => _taskCoordinator.CompleteTaskFailAsync(callerSessionId, reason, workerResult, ct, origin);

    public Task<JsonElement> AskUserAsync(
        string? callerSessionId,
        IReadOnlyList<AskQuestionDto> questions,
        CancellationToken ct = default, InteropPrincipal.Session? authorization = null)
        => _interactions.AskUserAsync(callerSessionId, questions, ct, authorization);

    public Task<MailDto> SendCrossTrackMailAsync(
        string callerSessionId,
        string? toTrackId,
        string summary,
        string body,
        Func<MailDto, Action?, CancellationToken, Task> deliverToTargetTrack,
        CancellationToken ct, string? replyToMailId, InteropPrincipal.Session? authorization = null, bool requireLead = false)
        => _interactions.SendCrossTrackMailAsync(callerSessionId, toTrackId, summary, body, deliverToTargetTrack, ct, replyToMailId, authorization, requireLead);

    internal Task ReceiveMailAsync(MailDto mail, CancellationToken ct = default, Action? authorizeDelivery = null)
        => _interactions.ReceiveMailAsync(mail, ct, authorizeDelivery);

    internal Task<MailDto> ReceiveDistillMailAsync(MailDto mail, bool idempotent, CancellationToken ct = default)
        => _interactions.ReceiveDistillMailAsync(mail, idempotent, ct);

    public Task<IReadOnlyList<MailDto>> ListMailsAsync(bool unreadOnly = false, CancellationToken ct = default)
        => _interactions.ListMailsAsync(unreadOnly, ct);

    public Task<MailDto> ReadMailAsync(string mailId, CancellationToken ct = default, InteropPrincipal.Session? authorization = null)
        => _interactions.ReadMailAsync(mailId, ct, authorization);

    public Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(CancellationToken ct = default)
        => _terminalLifecycle.ListTerminalsAsync(ct);

    public Task<TerminalDto> OpenTerminalAsync(string? callerSessionId, OpenTerminalRequest req, CancellationToken ct = default, InteropPrincipal.Session? authorization = null)
        => _terminalLifecycle.OpenTerminalAsync(callerSessionId, req, ct, authorization);

    public Task<TermObserveResult> ObserveTerminalAsync(TermObserveRequest req, CancellationToken ct = default)
        => _terminalLifecycle.ObserveTerminalAsync(req, ct);

    public Task<TermActionResult> SendTerminalInputAsync(TermActionRequest req, CancellationToken ct = default, InteropPrincipal.Session? authorization = null)
        => _terminalLifecycle.SendTerminalInputAsync(req, ct, authorization);

    public Task ResizeTerminalAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct = default)
        => _terminalLifecycle.ResizeTerminalAsync(terminalId, cols, rows, ct);

    public Task<TerminalDto> CloseTerminalAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default)
        => _terminalLifecycle.CloseTerminalAsync(terminalId, graceMs, ct);

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
        await HydrateMemoryScopeCoreAsync(ct).ConfigureAwait(false);
        _executionTargetId = _track.ExecutionTargetId;
        _reports.Hydrate(snapshot.Report);
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
        _taskCoordinator.ReplaceDeclarations(snapshot.Tasks);

        _sessions.Clear();
        foreach (var s in snapshot.Sessions)
        {
            var isLead = string.Equals(s.Id, _track.LeadSessionId, StringComparison.Ordinal);
            var normalized = s.IsLead == isLead ? s : s with { IsLead = isLead };
            _sessions[normalized.Id] = normalized;

            _nextTurnIndexBySession[normalized.Id] = await _store.GetLastTurnIndexAsync(normalized.Id, ct)
                .ConfigureAwait(false);
        }


        _interactions.Hydrate(snapshot.OpenAsks);
        var terminals = await _store.ListTerminalsAsync(TrackId, ct).ConfigureAwait(false);
        _terminalLifecycle.Hydrate(terminals);

        _workspacePreparation = await _store.GetWorkspacePreparationAsync(TrackId, ct).ConfigureAwait(false);
        await HydrateFlowCoreAsync(ct).ConfigureAwait(false);
        _hydrated = true;
    }

    private async Task<MountedWorkspace> EnsureWorkspaceMountedCoreAsync(CancellationToken ct)
    {
        RequireWorkspaceReadyCore();
        var driver = EffectiveWorkspaceDrivers.GetRequired(_track.WorkspaceDriver);
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

    internal Task TriggerSchedulerAsync(CancellationToken ct = default)
        => _taskCoordinator.TriggerSchedulerAsync(ct);

    private TrackDto PresentTrackCore() => TemplatePresentation.Project(_track, _policy, _policyVm);

    private TrackAggregateSnapshot BuildSnapshotCore(TrackDto track)
    {
        return new TrackAggregateSnapshot(
            Track: TemplatePresentation.Project(track, _policy, _policyVm),
            Report: _reports.Document,
            Tasks: _taskCoordinator.Snapshot(),
            Sessions: OrderedSessionsCore(),
            OpenAsks: _interactions.OpenAsks(),
            TemplateSnapshotSource: _templateSnapshotSource,
            WorkspacePreparation: WorkspacePreparationStatus) { PendingApprovals = ReadPendingApprovals() };
    }

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

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
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

        CancelAllActiveWatchesCore();
        await _runtimes.DisposeAllAsync(_options.SessionStartupTimeout).ConfigureAwait(false);

        _actorShutdownCts.Dispose();
    }


}
