using System.Text;
using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Actors;
using Perga.Kernel.Boot;
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

namespace Perga.Kernel;

public interface IPergaKernel : IAsyncDisposable
{
    IKernelEventBus EventBus { get; }
    ITemplateCatalog Templates { get; }
    IMcpSessionBridge McpSessionBridge { get; }
    TrackActorRegistry Actors { get; }
    IBootReconciler BootReconciler { get; }

    // System & Bootstrap
    Task<BootstrapDto> GetBootstrapAsync(CancellationToken ct = default);
    IReadOnlyList<CompiledTemplateManifest> ListTemplates();
    Task<BootReconcileResult> ReconcileOnBootAsync(CancellationToken ct = default);
    Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(CancellationToken ct = default);

    // Areas
    Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct = default);
    Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct = default);
    Task<AreaDto> CreateAreaAsync(CreateAreaRequest req, CancellationToken ct = default, string? requestKey = null);
    Task<AreaDto> PatchAreaAsync(string areaId, PatchAreaRequest req, CancellationToken ct = default);
    Task DeleteAreaAsync(string areaId, CancellationToken ct = default);

    // Tracks
    Task<IReadOnlyList<TrackDto>> ListTracksAsync(string? areaId = null, bool includeClosed = false, CancellationToken ct = default);
    Task<TrackAggregateSnapshot> CreateTrackAsync(CreateTrackRequest req, CancellationToken ct = default, string? requestKey = null);
    Task RetryWorkspaceAsync(string trackId, CancellationToken ct = default);
    Task<TrackAggregateSnapshot> GetTrackAggregateAsync(string trackId, CancellationToken ct = default);
    Task<TrackStatusDto> GetTrackStatusAsync(string trackId, CancellationToken ct = default);
    Task<TrackDto> PatchTrackAsync(string trackId, PatchTrackRequest req, CancellationToken ct = default);
    Task<TrackDto> RenameTrackAsync(string trackId, string callerSessionId, string newTitle, CancellationToken ct = default);
    Task<TrackDto> CloseTrackAsync(string trackId, string callerSessionId, CancellationToken ct = default);
    Task DeleteTrackAsync(string trackId, CancellationToken ct = default);

    // Workspace Files & VCS Diff
    Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(string trackId, string? relativePath = null, int maxBytes = 256 * 1024, CancellationToken ct = default);
    Task<DiffSummary> GetTrackVcsDiffAsync(string trackId, string? fromRev = null, string? toRev = null, CancellationToken ct = default);

    // Report & Revisions
    Task<ReportReadResult> ReadReportAsync(string trackId, string? callerSessionId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(string trackId, int docRev, CancellationToken ct = default);
    Task<ReportReadResult> UpdateReportBlockByHumanAsync(string trackId, string blockId, HumanBlockUpdateRequest req, CancellationToken ct = default);
    Task<ReportReadResult> CommitReportAsync(string trackId, string callerSessionId, ReportCommitRequest req, CancellationToken ct = default);

    // Tasks, Governance & Completion
    Task<IReadOnlyList<TaskDto>> ListTasksAsync(string trackId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskAttemptDto>> ListTaskAttemptsAsync(string trackId, string taskKeyOrId, CancellationToken ct = default);
    Task<TaskDto> CancelTaskAsync(string trackId, string callerSessionId, string taskKeyOrId, string? reason = null, CancellationToken ct = default);
    Task<TaskDto> AcceptTaskAsync(string trackId, string callerSessionId, string taskKeyOrId, string? note = null, CancellationToken ct = default);
    Task<TaskDto> RejectTaskAsync(string trackId, string callerSessionId, string taskKeyOrId, string note, bool reQueue = false, CancellationToken ct = default);
    Task<TaskDto> CompleteAttemptDoneAsync(string trackId, string callerSessionId, JsonElement? workerResult = null, CancellationToken ct = default);
    Task<TaskDto> CompleteAttemptFailAsync(string trackId, string callerSessionId, string reason, JsonElement? workerResult = null, CancellationToken ct = default);

    // Sessions, Messages & Epoch Compaction
    Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(string trackId, CancellationToken ct = default);
    Task<AgentSessionDto> CreateSubagentSessionAsync(string trackId, CreateSessionRequest req, CancellationToken ct = default);
    Task<(SessionCreationReceipt Receipt, bool Replay)> CreateSessionWithReceiptAsync(
        string trackId, CreateSessionRequest req, string key, CancellationToken ct);
    Task<(SessionInputReceipt? Receipt, SessionTurnDto? SteeredTurn, bool Replay)> AcceptMessageAsync(
        string sessionId, SendMessageRequest req, CancellationToken ct);
    Task<SessionInputReceipt> GetSessionInputAsync(string sessionId, string inputId, CancellationToken ct);
    Task<SessionInputReceipt> ResolveUnknownInputAsync(string sessionId, string inputId, CancellationToken ct);
    Task<SessionItemsPageDto> GetSessionItemsAsync(string sessionId, long afterId = 0, int limit = 100, CancellationToken ct = default, bool latest = false, long? beforeId = null);
    Task<SteeringResult> SteerMessageAsync(string sessionId, string turnId, string content, CancellationToken ct);

    Task<SessionTurnDto> SendMessageAsync(string sessionId, SendMessageRequest req, CancellationToken ct = default);
    Task InterruptSessionAsync(string sessionId, CancellationToken ct = default);
    Task<AgentSessionDto> CompactSessionAsync(string sessionId, CancellationToken ct = default);

    // Asks
    Task<JsonElement> AskUserAsync(string trackId, string? callerSessionId, IReadOnlyList<AskQuestionDto> questions, CancellationToken ct = default);
    Task<AskDto> AnswerAskAsync(string trackId, string askId, JsonElement answer, CancellationToken ct = default);

    // Cross-Track Mail
    Task<MailDto> SendMailAsync(string fromTrackId, string callerSessionId, string? toTrackId, string summary, string body, CancellationToken ct = default, string? replyToMailId = null);
    Task<IReadOnlyList<MailDto>> ListMailsAsync(string trackId, bool unreadOnly = false, CancellationToken ct = default);
    Task<MailDto> ReadMailAsync(string trackId, string mailId, CancellationToken ct = default);

    // Terminals
    Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(string trackId, CancellationToken ct = default);
    Task<TerminalDto> OpenTerminalAsync(string trackId, string? callerSessionId, OpenTerminalRequest req, CancellationToken ct = default);
    Task<TermObserveResult> ObserveTerminalAsync(string trackId, TermObserveRequest req, CancellationToken ct = default);
    Task<TermActionResult> SendTerminalInputAsync(string trackId, TermActionRequest req, CancellationToken ct = default);
    Task ResizeTerminalAsync(string trackId, string terminalId, ushort cols, ushort rows, CancellationToken ct = default);
    Task<TerminalDto> CloseTerminalAsync(string trackId, string terminalId, int graceMs = 2000, CancellationToken ct = default);
    Uri GetTerminalWebSocketRelayUri(string terminalId);

    // MCP Policy Guard, Tool Dispatch & Schedules
    Task GuardMcpToolCallAsync(string trackId, string sessionId, string mcpServer, string toolName, JsonElement arguments, CancellationToken ct = default);
    Task<JsonElement> InvokeKernelMcpToolAsync(McpSessionPrincipal caller, string toolName, JsonElement arguments, CancellationToken ct = default);
    Task TriggerScheduleWakeAsync(string trackId, string scheduleId, CancellationToken ct = default);

    // Synchronization helper for tests / shutdown
    Task WaitForTrackIdleAsync(string trackId, CancellationToken ct = default);
}

public sealed class PergaKernelService : IPergaKernel
{
    private readonly ITrackStore _store;
    private readonly IReportDocEngine _reportEngine;
    private readonly IPolicyVm _policyVm;
    private readonly IAgentRuntime _agentRuntime;
    private readonly IWorkspaceDriverRegistry _workspaceDrivers;
    private readonly IVcsService _vcsService;
    private readonly ITerminalBridgeClient _terminalClient;
    private readonly IShellCommandRunner _commandRunner;
    private readonly EventHub _eventHub;
    private readonly KernelOptions _options;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public PergaKernelService(
        ITrackStore store,
        IReportDocEngine reportEngine,
        IPolicyVm policyVm,
        IAgentRuntime agentRuntime,
        IWorkspaceDriverRegistry workspaceDrivers,
        IVcsService vcsService,
        ITerminalBridgeClient terminalClient,
        KernelOptions? options = null,
        ITemplateCatalog? templateCatalog = null,
        IMcpSessionBridge? mcpSessionBridge = null,
        IShellCommandRunner? commandRunner = null,
        EventHub? eventHub = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reportEngine = reportEngine ?? throw new ArgumentNullException(nameof(reportEngine));
        _policyVm = policyVm ?? throw new ArgumentNullException(nameof(policyVm));
        _agentRuntime = agentRuntime ?? throw new ArgumentNullException(nameof(agentRuntime));
        _workspaceDrivers = workspaceDrivers ?? throw new ArgumentNullException(nameof(workspaceDrivers));
        _vcsService = vcsService ?? throw new ArgumentNullException(nameof(vcsService));
        _terminalClient = terminalClient ?? throw new ArgumentNullException(nameof(terminalClient));
        _options = options ?? new KernelOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _commandRunner = commandRunner ?? new ProcessShellCommandRunner();
        _eventHub = eventHub ?? new EventHub();

        Templates = templateCatalog ?? new TemplateCatalog(_policyVm, _options.TemplatesDirectory);
        McpSessionBridge = mcpSessionBridge ?? new InMemoryMcpSessionBridge(_options.McpBaseUri);
        Actors = new TrackActorRegistry(
            _store,
            _reportEngine,
            _policyVm,
            Templates,
            _agentRuntime,
            _workspaceDrivers,
            _terminalClient,
            McpSessionBridge,
            _commandRunner,
            _eventHub,
            _options,
            _timeProvider);
        BootReconciler = new BootReconciler(_store, _workspaceDrivers, Actors);
    }

    public IKernelEventBus EventBus => _eventHub;
    public ITemplateCatalog Templates { get; }
    public IMcpSessionBridge McpSessionBridge { get; }
    public TrackActorRegistry Actors { get; }
    public IBootReconciler BootReconciler { get; }

    // ------------------- System & Bootstrap -------------------

    public async Task<BootstrapDto> GetBootstrapAsync(CancellationToken ct = default)
    {
        var providersTask = _agentRuntime.ProbeProvidersAsync(ct);
        var areasTask = _store.ListAreasAsync(ct);
        await Task.WhenAll(providersTask, areasTask).ConfigureAwait(false);

        return new BootstrapDto(
            Version: _options.Version,
            Providers: await providersTask.ConfigureAwait(false),
            Templates: Templates.ListManifests(),
            Areas: await areasTask.ConfigureAwait(false));
    }

    public IReadOnlyList<CompiledTemplateManifest> ListTemplates() => Templates.ListManifests();

    public Task<BootReconcileResult> ReconcileOnBootAsync(CancellationToken ct = default) =>
        BootReconciler.ReconcileAsync(ct);

    public Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(CancellationToken ct = default) =>
        _store.RunMaintenancePassAsync(NowMs(), ct);

    // ------------------- Areas -------------------

    public Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct = default) =>
        _store.ListAreasAsync(ct);

    public Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct = default) =>
        _store.GetAreaAsync(areaId, ct);

    public async Task<AreaDto> CreateAreaAsync(CreateAreaRequest req, CancellationToken ct = default, string? requestKey = null)
    {
        var key = CreateKey("area", "", requestKey, req);
        if (key is not null && await _store.FindCreationAsync(key, ct).ConfigureAwait(false) is { } replay)
            return await ReadCreatedAreaAsync(replay, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            throw KernelException.InvalidArgument("Area name cannot be empty.");
        }

        var now = NowMs();
        var area = new AreaDto(
            Id: $"area_{Guid.NewGuid():N}",
            Name: req.Name.Trim(),
            Color: string.IsNullOrWhiteSpace(req.Color) ? "#64748b" : req.Color!,
            DefaultCwd: req.DefaultCwd,
            Folders: req.Folders?.ToArray() ?? [],
            SortOrder: req.SortOrder ?? 0.0,
            CreatedAtMs: now,
            UpdatedAtMs: now);

        var id = await _store.CreateAreaAsync(area, key, ct).ConfigureAwait(false);
        return await ReadCreatedAreaAsync(id, ct).ConfigureAwait(false);
    }

    private async Task<AreaDto> ReadCreatedAreaAsync(string id, CancellationToken ct) =>
        await _store.GetAreaAsync(id, ct).ConfigureAwait(false) ??
        throw new KernelException(KernelErrorCode.IdempotencyKeyExhausted, "The created Area was deleted.");

    private static ResourceCreationKey? CreateKey<T>(string operation, string scope, string? key, T request)
    {
        if (key is null)
            return null;
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
            throw new KernelException(KernelErrorCode.IdempotencyKeyInvalid, "Idempotency-Key must contain 1 to 256 characters.");
        return new(operation, scope, key, RequestIdentity.Hash(new { Version = 1, Request = request }));
    }

    public async Task<AreaDto> PatchAreaAsync(string areaId, PatchAreaRequest req, CancellationToken ct = default)
    {
        var existing = await _store.GetAreaAsync(areaId, ct).ConfigureAwait(false)
                       ?? throw KernelException.NotFound($"Area '{areaId}' not found.");

        var now = NowMs();
        var nextDefaultCwd = req.ClearDefaultCwd ? null : (req.DefaultCwd ?? existing.DefaultCwd);
        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(req.Name) ? existing.Name : req.Name!.Trim(),
            Color = string.IsNullOrWhiteSpace(req.Color) ? existing.Color : req.Color!,
            DefaultCwd = nextDefaultCwd,
            Folders = req.Folders?.ToArray() ?? existing.Folders,
            SortOrder = req.SortOrder ?? existing.SortOrder,
            UpdatedAtMs = now
        };

        return await _store.UpsertAreaAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task DeleteAreaAsync(string areaId, CancellationToken ct = default)
    {
        _ = await _store.GetAreaAsync(areaId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Area '{areaId}' not found.");

        // Cleanly tear down child tracks so workspaces, processes, and actors do not leak.
        var childTracks = await _store.ListTracksAsync(areaId, includeClosed: true, ct).ConfigureAwait(false);
        foreach (var track in childTracks)
        {
            await DeleteTrackAsync(track.Id, ct).ConfigureAwait(false);
        }

        await _store.DeleteAreaAsync(areaId, ct, requireEmpty: true).ConfigureAwait(false);
    }

    // ------------------- Tracks -------------------

    public Task<IReadOnlyList<TrackDto>> ListTracksAsync(
        string? areaId = null,
        bool includeClosed = false,
        CancellationToken ct = default) =>
        _store.ListTracksAsync(areaId, includeClosed, ct);

    public async Task<TrackAggregateSnapshot> CreateTrackAsync(CreateTrackRequest req, CancellationToken ct = default, string? requestKey = null)
    {
        var key = CreateKey("track", req.AreaId, requestKey, req);
        if (key is not null && await _store.FindCreationAsync(key, ct).ConfigureAwait(false) is { } replay)
            return await ReadCreatedTrackAsync(replay, ct).ConfigureAwait(false);

        var area = await _store.GetAreaAsync(req.AreaId, ct).ConfigureAwait(false)
                   ?? throw KernelException.NotFound($"Area '{req.AreaId}' not found.");

        CompiledPolicy? policy = null;
        if (!string.IsNullOrWhiteSpace(req.TemplateId))
        {
            policy = Templates.GetRequiredPolicy(req.TemplateId!);
        }

        var trackId = $"trk_{Guid.NewGuid():N}";
        var leadSessionId = $"ses_{Guid.NewGuid():N}";
        var now = NowMs();

        var driverId = !string.IsNullOrWhiteSpace(req.WorkspaceDriver)
            ? req.WorkspaceDriver!
            : !string.IsNullOrWhiteSpace(policy?.Manifest.WorkspaceDriver)
                ? policy!.Manifest.WorkspaceDriver!
                : _options.DefaultWorkspaceDriver;

        var sourcePath = !string.IsNullOrWhiteSpace(req.Cwd)
            ? req.Cwd!
            : !string.IsNullOrWhiteSpace(area.DefaultCwd)
                ? area.DefaultCwd!
                : Directory.GetCurrentDirectory();

        var targetRootDir = Path.Combine(_options.WorkspacesRootDirectory, trackId);
        var lowerCacheDirs = policy?.Manifest.WorkspaceLowerCacheDirs ?? [];

        _workspaceDrivers.GetRequired(driverId);
        var preparation = new WorkspacePreparation(
            new WorkspaceProvisionRequest(trackId, Path.GetFullPath(sourcePath), Path.GetFullPath(targetRootDir), lowerCacheDirs, req.BaseRevision),
            policy?.Manifest.WorkspaceCreateCmd);

        var templateInputs = JsonHelpers.EnsureClonedObject(req.TemplateInputs);
        var templateState = JsonHelpers.EmptyObject;
        if (policy is not null)
        {
            var initEval = _policyVm.InitState(policy, templateInputs);
            templateState = initEval.Output.ValueKind != JsonValueKind.Undefined
                ? initEval.Output.Clone()
                : initEval.NextState.Clone();
        }

        var initialMarkdown = policy?.Manifest.InitialReportMarkdown ?? "# Overview\n\nTrack initialized.\n";
        var initialReport = _reportEngine.Parse(initialMarkdown, docRev: 0, summary: string.Empty);

        var enabledPlugins = req.EnabledPlugins?.ToArray()
                             ?? policy?.Manifest.DefaultPlugins.ToArray()
                             ?? [];

        var title = !string.IsNullOrWhiteSpace(req.Title)
            ? req.Title!.Trim()
            : policy?.Manifest.Title ?? "Untitled Track";

        var track = new TrackDto(
            Id: trackId,
            AreaId: area.Id,
            ParentTrackId: req.ParentTrackId,
            ParentAttemptId: req.ParentAttemptId,
            Title: title,
            Cwd: string.Empty,
            WorkspaceDriver: driverId,
            WorkspaceSpec: JsonHelpers.EmptyObject,
            LeadSessionId: leadSessionId,
            TemplateId: policy?.TemplateId,
            TemplateInputs: templateInputs,
            TemplateState: templateState,
            EnabledPlugins: enabledPlugins,
            ClosedAtMs: null,
            PinnedAtMs: null,
            SortOrder: req.SortOrder ?? 0.0,
            CreatedAtMs: now,
            UpdatedAtMs: now);

        var firstInput = string.IsNullOrWhiteSpace(req.FirstMessage) ? null
            : new SessionInputRequest($"init:{trackId}",
                RequestIdentity.Hash(new { version = 1, Content = req.FirstMessage, SteerIfRunning = false }), req.FirstMessage);

        var leadSession = new AgentSessionDto(
            Id: leadSessionId,
            TrackId: trackId,
            IsLead: true,
            BoundTaskId: null,
            BoundAttemptId: null,
            Title: "Lead",
            Provider: string.IsNullOrWhiteSpace(req.Provider) ? _options.DefaultProvider : req.Provider!,
            Model: req.Model ?? _options.DefaultModel,
            NativeThreadId: null,
            Epoch: 1,
            Status: firstInput is null ? "idle" : "queued",
            ContextUsageRatio: 0.0,
            ArchivedAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now);

        var initialTasks = TaskBlockSynchronizer.SyncTasksFromReport(
            trackId, initialReport, existingTasks: [],
            requireGateForReadWrite: policy?.Manifest.RequireGateForReadWrite ?? false, nowMs: now).SyncedTasks;
        EventEnvelopeDto[] events =
        [
            new(null, area.Id, trackId, KernelEventKinds.TrackUpdated, JsonHelpers.ToElement(track), now),
            new(null, area.Id, trackId, KernelEventKinds.SessionUpdated, JsonHelpers.ToElement(leadSession), now)
        ];

        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var id = await _store.CreateTrackAsync(
            new(track, initialReport, policy?.JsSource, leadSession, initialTasks, firstInput, preparation, events),
            key, ct).ConfigureAwait(false);
        var snapshot = await ReadCreatedTrackAsync(id, CancellationToken.None).ConfigureAwait(false);
        if (id == track.Id)
        {
            foreach (var evt in events)
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None, allowFallback: false).ConfigureAwait(false);
        }
        return snapshot;
    }

    private async Task<TrackAggregateSnapshot> ReadCreatedTrackAsync(string id, CancellationToken ct)
    {
        var actor = Actors.GetOrActivate(id);
        try
        {
            return await actor.GetAggregateSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (KernelException e) when (e.Code == KernelErrorCode.NotFound)
        {
            throw new KernelException(KernelErrorCode.IdempotencyKeyExhausted, "The created Track was deleted.");
        }
    }

    public Task RetryWorkspaceAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).RetryWorkspaceAsync(ct);

    public Task<TrackAggregateSnapshot> GetTrackAggregateAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).GetAggregateSnapshotAsync(ct);

    public Task<TrackStatusDto> GetTrackStatusAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).GetStatusAsync(ct);

    public Task<TrackDto> PatchTrackAsync(string trackId, PatchTrackRequest req, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).PatchTrackAsync(req, ct);

    public Task<TrackDto> RenameTrackAsync(
        string trackId,
        string callerSessionId,
        string newTitle,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).RenameTrackBySessionAsync(callerSessionId, newTitle, ct);

    public Task<TrackDto> CloseTrackAsync(string trackId, string callerSessionId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CloseTrackBySessionAsync(callerSessionId, ct);

    public async Task DeleteTrackAsync(string trackId, CancellationToken ct = default)
    {
        var actor = Actors.GetOrActivate(trackId);
        await actor.TeardownAndDeleteTrackAsync(ct).ConfigureAwait(false);
        await Actors.RemoveAndDisposeAsync(trackId).ConfigureAwait(false);
    }

    // ------------------- Workspace Files & VCS Diff -------------------

    public async Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(
        string trackId,
        string? relativePath = null,
        int maxBytes = 256 * 1024,
        CancellationToken ct = default)
    {
        var mounted = await Actors.GetOrActivate(trackId).EnsureMountedAsync(ct).ConfigureAwait(false);
        var rootFull = Path.GetFullPath(mounted.MergedCwd);
        var sanitizedRel = string.IsNullOrWhiteSpace(relativePath) ? string.Empty : relativePath.TrimStart('/', '\\');
        var candidateFull = Path.GetFullPath(Path.Combine(rootFull, sanitizedRel));

        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!string.Equals(candidateFull, rootFull, StringComparison.Ordinal) &&
            !candidateFull.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw KernelException.Forbidden("Path escapes workspace root.");
        }

        if (Directory.Exists(candidateFull))
        {
            var entries = new List<WorkspaceFileEntryDto>();
            foreach (var dir in Directory.GetDirectories(candidateFull).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                var rel = Path.GetRelativePath(rootFull, dir).Replace('\\', '/');
                entries.Add(new WorkspaceFileEntryDto(name, rel, IsDirectory: true, SizeBytes: 0));
            }

            foreach (var file in Directory.GetFiles(candidateFull).OrderBy(f => f, StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                var rel = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
                entries.Add(new WorkspaceFileEntryDto(info.Name, rel, IsDirectory: false, SizeBytes: info.Length));
            }

            var relDir = string.Equals(candidateFull, rootFull, StringComparison.Ordinal)
                ? string.Empty
                : Path.GetRelativePath(rootFull, candidateFull).Replace('\\', '/');
            return new WorkspaceFileResultDto(relDir, IsDirectory: true, Content: null, Truncated: false, Entries: entries);
        }

        if (!File.Exists(candidateFull))
        {
            throw KernelException.NotFound($"Workspace path '{sanitizedRel}' not found.");
        }

        await using var fs = new FileStream(candidateFull, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var limit = Math.Max(1, maxBytes);
        var buffer = new byte[Math.Min(fs.Length, limit)];
        var read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        var truncated = fs.Length > read;
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var relFile = Path.GetRelativePath(rootFull, candidateFull).Replace('\\', '/');

        return new WorkspaceFileResultDto(relFile, IsDirectory: false, Content: text, Truncated: truncated, Entries: null);
    }

    public async Task<DiffSummary> GetTrackVcsDiffAsync(
        string trackId,
        string? fromRev = null,
        string? toRev = null,
        CancellationToken ct = default)
    {
        var mounted = await Actors.GetOrActivate(trackId).EnsureMountedAsync(ct).ConfigureAwait(false);
        return await _vcsService.DiffAsync(mounted.MergedCwd, fromRev, toRev, ct).ConfigureAwait(false);
    }

    // ------------------- Report & Revisions -------------------

    public Task<ReportReadResult> ReadReportAsync(
        string trackId,
        string? callerSessionId = null,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ReadReportAsync(callerSessionId, ct);

    public Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(
        string trackId,
        int docRev,
        CancellationToken ct = default) =>
        _store.GetReportRevisionBlocksAsync(trackId, docRev, ct);

    public Task<ReportReadResult> UpdateReportBlockByHumanAsync(
        string trackId,
        string blockId,
        HumanBlockUpdateRequest req,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).UpdateReportBlockByHumanAsync(blockId, req, ct);

    public Task<ReportReadResult> CommitReportAsync(
        string trackId,
        string callerSessionId,
        ReportCommitRequest req,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CommitReportBySessionAsync(callerSessionId, req, ct);

    // ------------------- Tasks, Governance & Completion -------------------

    public Task<IReadOnlyList<TaskDto>> ListTasksAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ListTasksAsync(ct);

    public Task<IReadOnlyList<TaskAttemptDto>> ListTaskAttemptsAsync(
        string trackId,
        string taskKeyOrId,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ListTaskAttemptsAsync(taskKeyOrId, ct);

    public Task<TaskDto> CancelTaskAsync(
        string trackId,
        string callerSessionId,
        string taskKeyOrId,
        string? reason = null,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CancelTaskAsync(callerSessionId, taskKeyOrId, reason, ct);

    public Task<TaskDto> AcceptTaskAsync(
        string trackId,
        string callerSessionId,
        string taskKeyOrId,
        string? note = null,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).AcceptTaskAsync(callerSessionId, taskKeyOrId, note, ct);

    public Task<TaskDto> RejectTaskAsync(
        string trackId,
        string callerSessionId,
        string taskKeyOrId,
        string note,
        bool reQueue = false,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).RejectTaskAsync(callerSessionId, taskKeyOrId, note, reQueue, ct);

    public Task<TaskDto> CompleteAttemptDoneAsync(
        string trackId,
        string callerSessionId,
        JsonElement? workerResult = null,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CompleteAttemptDoneAsync(callerSessionId, workerResult, ct);

    public Task<TaskDto> CompleteAttemptFailAsync(
        string trackId,
        string callerSessionId,
        string reason,
        JsonElement? workerResult = null,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CompleteAttemptFailAsync(callerSessionId, reason, workerResult, ct);

    // ------------------- Sessions, Messages & Epoch Compaction -------------------

    public Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ListSessionsAsync(ct);

    public Task<AgentSessionDto> CreateSubagentSessionAsync(
        string trackId,
        CreateSessionRequest req,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CreateSubagentSessionAsync(req, ct);

    public Task<(SessionCreationReceipt Receipt, bool Replay)> CreateSessionWithReceiptAsync(
        string trackId, CreateSessionRequest req, string key, CancellationToken ct) =>
        Actors.GetOrActivate(trackId).CreateSessionWithReceiptAsync(req, key, ct);

    public async Task<(SessionInputReceipt? Receipt, SessionTurnDto? SteeredTurn, bool Replay)> AcceptMessageAsync(
        string sessionId, SendMessageRequest req, CancellationToken ct)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).AcceptMessageAsync(sessionId, req, ct).ConfigureAwait(false);
    }

    public async Task<SessionInputReceipt> GetSessionInputAsync(string sessionId, string inputId, CancellationToken ct)
    {
        var input = await _store.SessionInputs.FindByIdAsync(sessionId, inputId, ct).ConfigureAwait(false);
        return input?.Receipt ?? throw KernelException.NotFound("Session input not found.");
    }

    public async Task<SessionInputReceipt> ResolveUnknownInputAsync(string sessionId, string inputId, CancellationToken ct)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).ResolveUnknownInputAsync(sessionId, inputId, ct).ConfigureAwait(false);
    }

    public async Task<SessionItemsPageDto> GetSessionItemsAsync(
        string sessionId,
        long afterId = 0,
        int limit = 100,
        CancellationToken ct = default, bool latest = false, long? beforeId = null)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).GetSessionItemsAsync(sessionId, afterId, limit, ct, latest, beforeId)
            .ConfigureAwait(false);
    }

    public async Task<SteeringResult> SteerMessageAsync(string sessionId, string turnId, string content, CancellationToken ct)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).SteerMessageAsync(sessionId, turnId, content, ct).ConfigureAwait(false);
    }

    public async Task<SessionTurnDto> SendMessageAsync(
        string sessionId,
        SendMessageRequest req,
        CancellationToken ct = default)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).SendMessageAsync(sessionId, req, ct).ConfigureAwait(false);
    }

    public async Task InterruptSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        await Actors.GetOrActivate(trackId).InterruptSessionAsync(sessionId, ct).ConfigureAwait(false);
    }

    public async Task<AgentSessionDto> CompactSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).CompactSessionAsync(sessionId, ct).ConfigureAwait(false);
    }

    // ------------------- Asks -------------------

    public Task<JsonElement> AskUserAsync(
        string trackId,
        string? callerSessionId,
        IReadOnlyList<AskQuestionDto> questions,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).AskUserAsync(callerSessionId, questions, ct);

    public Task<AskDto> AnswerAskAsync(
        string trackId,
        string askId,
        JsonElement answer,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).AnswerAskAsync(askId, answer, ct);

    // ------------------- Cross-Track Mail -------------------

    public Task<MailDto> SendMailAsync(
        string fromTrackId,
        string callerSessionId,
        string? toTrackId,
        string summary,
        string body,
        CancellationToken ct = default,
        string? replyToMailId = null)
    {
        var senderActor = Actors.GetOrActivate(fromTrackId);
        return senderActor.SendCrossTrackMailAsync(
            callerSessionId,
            toTrackId,
            summary,
            body,
            deliverToTargetTrack: async (mail, token) =>
            {
                var targetActor = Actors.GetOrActivate(mail.ToTrackId);
                await targetActor.ReceiveMailAsync(mail, token).ConfigureAwait(false);
            },
            ct, replyToMailId);
    }

    public Task<IReadOnlyList<MailDto>> ListMailsAsync(
        string trackId,
        bool unreadOnly = false,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ListMailsAsync(unreadOnly, ct);

    public Task<MailDto> ReadMailAsync(string trackId, string mailId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ReadMailAsync(mailId, ct);

    // ------------------- Terminals -------------------

    public Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ListTerminalsAsync(ct);

    public Task<TerminalDto> OpenTerminalAsync(
        string trackId,
        string? callerSessionId,
        OpenTerminalRequest req,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).OpenTerminalAsync(callerSessionId, req, ct);

    public Task<TermObserveResult> ObserveTerminalAsync(
        string trackId,
        TermObserveRequest req,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ObserveTerminalAsync(req, ct);

    public Task<TermActionResult> SendTerminalInputAsync(
        string trackId,
        TermActionRequest req,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).SendTerminalInputAsync(req, ct);

    public Task ResizeTerminalAsync(
        string trackId,
        string terminalId,
        ushort cols,
        ushort rows,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ResizeTerminalAsync(terminalId, cols, rows, ct);

    public Task<TerminalDto> CloseTerminalAsync(
        string trackId,
        string terminalId,
        int graceMs = 2000,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CloseTerminalAsync(terminalId, graceMs, ct);

    public Uri GetTerminalWebSocketRelayUri(string terminalId) =>
        _terminalClient.GetWebSocketRelayUri(terminalId);

    // ------------------- MCP Policy Guard, Tool Dispatch & Schedules -------------------

    public Task GuardMcpToolCallAsync(
        string trackId,
        string sessionId,
        string mcpServer,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).GuardMcpToolCallAsync(sessionId, mcpServer, toolName, arguments, ct);

    public Task<JsonElement> InvokeKernelMcpToolAsync(
        McpSessionPrincipal caller,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default) =>
        KernelMcpDispatcher.ExecuteGuardedAsync(this, caller, toolName, arguments, ct);

    public Task TriggerScheduleWakeAsync(string trackId, string scheduleId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).TriggerScheduleWakeAsync(scheduleId, ct);

    public Task WaitForTrackIdleAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).WaitForBackgroundWorkAsync(ct);

    private async Task<string> ResolveTrackIdForSessionAsync(string sessionId, CancellationToken ct)
    {
        var trackId = await _store.FindTrackIdBySessionAsync(sessionId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(trackId))
        {
            throw KernelException.NotFound($"Session '{sessionId}' was not found.");
        }

        return trackId;
    }

    private long NowMs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Actors.DisposeAsync().ConfigureAwait(false);
        _eventHub.Dispose();
    }
}
