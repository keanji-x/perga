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
    Task<BootstrapDto> GetBootstrapAsync(string? scope, bool refresh = false, CancellationToken ct = default);
    Task<TrackOverlaysResponseDto> GetOverlaysAsync(bool includeClosed = false, CancellationToken ct = default);
    HostListDirResultDto ListHostDirectory(string? path = null);
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
    Task<TrackWorkspaceOptionsDto> GetTrackWorkspaceOptionsAsync(string areaId, string? cwd, string? templateId, CancellationToken ct);
    Task RetryWorkspaceAsync(string trackId, CancellationToken ct = default);
    Task<TrackAggregateSnapshot> GetTrackAggregateAsync(string trackId, CancellationToken ct = default);
    Task<TrackStatusDto> GetTrackStatusAsync(string trackId, CancellationToken ct = default);
    Task<string?> GetTrackLeadSessionIdAsync(string trackId, CancellationToken ct);
    Task<TrackDto> PatchTrackAsync(string trackId, PatchTrackRequest req, CancellationToken ct = default);
    Task<TrackDto> RenameTrackAsync(string trackId, string callerSessionId, string newTitle, CancellationToken ct = default);
    Task<TrackDto> CloseTrackAsync(string trackId, string callerSessionId, CancellationToken ct = default);
    Task DeleteTrackAsync(string trackId, CancellationToken ct = default);

    // Workspace Files & VCS Diff
    Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(string trackId, string? relativePath = null, int maxBytes = 256 * 1024, CancellationToken ct = default);
    Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(string trackId, string? relativePath, CancellationToken ct = default);
    Task<DiffSummary> GetTrackVcsDiffAsync(string trackId, string? fromRev = null, string? toRev = null, CancellationToken ct = default);
    Task<DiffSummary> GetTrackVcsDiffAsync(string trackId, string? fromRev, string? toRev, string? path, CancellationToken ct = default);

    // Report & Revisions
    Task<ReportReadResult> ReadReportAsync(string trackId, string? callerSessionId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(string trackId, int docRev, CancellationToken ct = default);
    Task<ReportReadResult> UpdateReportBlockByHumanAsync(string trackId, string blockId, HumanBlockUpdateRequest req, CancellationToken ct = default);
    Task<ReportReadResult> CommitReportAsync(string trackId, string callerSessionId, ReportCommitRequest req, CancellationToken ct = default);

    // Tasks, Governance & Completion
    Task<IReadOnlyList<TaskDto>> ListTasksAsync(string trackId, CancellationToken ct = default);
    Task<WorkerDispatchReceipt> DispatchWorkerAsync(string trackId, DispatchWorkerRequest request, string? requestKey, McpSessionPrincipal? caller, CancellationToken ct);
    Task<IReadOnlyList<TaskAttemptDto>> ListTaskAttemptsAsync(string trackId, string taskKeyOrId, CancellationToken ct = default);
    Task<TaskDto> CancelTaskAsync(string trackId, string? callerSessionId, string taskKeyOrId, string? reason = null, CancellationToken ct = default);
    Task<TaskDto> AcceptTaskAsync(string trackId, string? callerSessionId, string taskKeyOrId, string? note = null, CancellationToken ct = default);
    Task<TaskDto> RejectTaskAsync(string trackId, string? callerSessionId, string taskKeyOrId, string note, bool reQueue = false, CancellationToken ct = default);
    Task<TaskDto> CompleteAttemptDoneAsync(string trackId, string callerSessionId, JsonElement? workerResult = null, CancellationToken ct = default, WorkerCompletionOrigin? origin = null);
    Task<TaskDto> CompleteAttemptFailAsync(string trackId, string callerSessionId, string reason, JsonElement? workerResult = null, CancellationToken ct = default, WorkerCompletionOrigin? origin = null);

    // Sessions, Messages & Epoch Compaction
    Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(string trackId, CancellationToken ct = default);
    Task<AgentSessionDto> CreateSubagentSessionAsync(string trackId, CreateSessionRequest req, CancellationToken ct = default);
    Task<(SessionCreationReceipt Receipt, bool Replay)> CreateSessionWithReceiptAsync(
        string trackId, CreateSessionRequest req, string key, CancellationToken ct);
    Task<AgentSessionDto> PatchSessionAsync(string sessionId, PatchSessionRequest req, CancellationToken ct = default);
    Task<MessageAcceptance> AcceptMessageAsync(
        string sessionId, SendMessageRequest req, CancellationToken ct);
    Task<AgentSessionDto> ResumeProviderSessionAsync(string sessionId, string turnId, CancellationToken ct);
    Task<SessionInputReceipt> GetSessionInputAsync(string sessionId, string inputId, CancellationToken ct);
    Task<SessionInputReceipt> ResolveUnknownInputAsync(string sessionId, string inputId, CancellationToken ct);
    Task<SessionItemsPageDto> GetSessionItemsAsync(string sessionId, long afterId = 0, int limit = 100,
        CancellationToken ct = default, bool latest = false, long? beforeId = null, McpSessionPrincipal? caller = null,
        string? activityTurnId = null, long? afterSequence = null, int waitMs = 0);
    Task<WorkerMessageReceipt> SendWorkerMessageAsync(string sessionId, WorkerMessageRequest request, McpSessionPrincipal? caller, CancellationToken ct);
    Task InterruptWorkerAsync(string sessionId, string attemptId, string turnId, McpSessionPrincipal? caller, CancellationToken ct);
    Task<WorkerSettings> UpdateWorkerSettingsAsync(string sessionId, UpdateWorkerSettingsRequest request, McpSessionPrincipal? caller,
        CancellationToken ct, bool verifiedApprovalOwner = false);
    Task<SteeringResult> SteerQueuedMessageAsync(string sessionId, string inputId, string turnId, CancellationToken ct,
        McpSessionPrincipal? caller = null, string? attemptId = null);
    Task RemoveQueuedMessageAsync(string sessionId, string inputId, CancellationToken ct, McpSessionPrincipal? caller = null, string? attemptId = null);
    Task<SteeringResult> SteerMessageAsync(string sessionId, string turnId, string content, CancellationToken ct, string? requestKey = null);

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

public sealed partial class PergaKernelService : IPergaKernel
{
    private const long ProviderProbeCacheTtlMs = 60_000L;
    private const int BinarySniffBytes = 8 * 1024;
    private static readonly string ProcessDbInstanceId = Guid.NewGuid().ToString("N");

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
    private readonly string _fallbackDatabaseId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _providerProbeLock = new(1, 1);

    private IReadOnlyList<ProviderStatus>? _cachedProviders;
    private long _cachedProvidersAtMs;
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

    public Task<BootstrapDto> GetBootstrapAsync(CancellationToken ct = default) =>
        GetBootstrapAsync(scope: null, refresh: false, ct);

    public async Task<BootstrapDto> GetBootstrapAsync(
        string? scope,
        bool refresh = false,
        CancellationToken ct = default)
    {
        var isWhoAmIScope = string.Equals(scope?.Trim(), "whoami", StringComparison.OrdinalIgnoreCase);
        var areasTask = _store.ListAreasAsync(ct);

        IReadOnlyList<ProviderStatus> providers;
        if (isWhoAmIScope)
        {
            providers = [];
            await areasTask.ConfigureAwait(false);
        }
        else
        {
            var providersTask = GetCachedOrProbeProvidersAsync(refresh, ct);
            await Task.WhenAll(providersTask, areasTask).ConfigureAwait(false);
            providers = await providersTask.ConfigureAwait(false);
        }

        var databaseId = !string.IsNullOrWhiteSpace(_store.DatabaseId)
            ? _store.DatabaseId
            : _fallbackDatabaseId;

        return new BootstrapDto(
            Version: _options.Version,
            Providers: providers,
            Templates: Templates.ListManifests(),
            Areas: await areasTask.ConfigureAwait(false),
            DatabaseId: databaseId,
            DbInstanceId: ProcessDbInstanceId,
            AuthRequired: false);
    }

    public async Task<TrackOverlaysResponseDto> GetOverlaysAsync(
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        var stored = await _store.ListTrackOverlaysAsync(includeClosed, ct).ConfigureAwait(false);
        var merged = new List<TrackOverlayDto>(stored.Count);
        foreach (var item in stored)
        {
            if (Actors.TryGetActive(item.TrackId, out var actor) && actor is not null)
            {
                var isWorking = item.IsWorking || actor.IsSessionOrTaskRunning;
                var updatedAtMs = Math.Max(item.UpdatedAtMs, actor.LastActivityAtMs);
                merged.Add(item with { IsWorking = isWorking, UpdatedAtMs = updatedAtMs });
            }
            else
            {
                merged.Add(item);
            }
        }

        return new TrackOverlaysResponseDto(merged);
    }

    public HostListDirResultDto ListHostDirectory(string? path = null)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homeDir))
        {
            homeDir = Environment.GetEnvironmentVariable("HOME") ?? Path.DirectorySeparatorChar.ToString();
        }

        string rawTarget;
        if (string.IsNullOrWhiteSpace(path))
        {
            rawTarget = homeDir;
        }
        else
        {
            var trimmed = path.Trim();
            if (string.Equals(trimmed, "~", StringComparison.Ordinal))
            {
                rawTarget = homeDir;
            }
            else if (trimmed.StartsWith("~/", StringComparison.Ordinal) ||
                     trimmed.StartsWith("~\\", StringComparison.Ordinal))
            {
                rawTarget = Path.Combine(homeDir, trimmed[2..]);
            }
            else
            {
                rawTarget = trimmed;
            }
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawTarget);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw KernelException.InvalidArgument($"Invalid directory path '{rawTarget}': {ex.Message}");
        }

        if (File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw KernelException.InvalidArgument($"Path '{fullPath}' is a file, not a directory.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw KernelException.NotFound($"Directory '{fullPath}' not found.");
        }

        // 1. Verify the target directory itself is readable (throws 403 on chmod 000 target dir,
        // without stat'ing child entries yet).
        try
        {
            using var openCheck = Directory
                .EnumerateFileSystemEntries(fullPath, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false
                })
                .GetEnumerator();
            _ = openCheck;
        }
        catch (UnauthorizedAccessException)
        {
            throw KernelException.Forbidden($"Access denied reading directory '{fullPath}'.");
        }

        // 2. Enumerate child paths as strings (AttributesToSkip = 0 avoids stat inside MoveNext()),
        //    then inspect each child inside a per-entry try/catch so FUSE mounts (/tmp/.mount_*) or
        //    root-owned dirs (/tmp/systemd-private-*, /root) never fail the parent listing.
        var entries = new List<HostDirEntryDto>();
        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };
        foreach (var childFullPath in Directory.EnumerateFileSystemEntries(fullPath, "*", enumOptions))
        {
            try
            {
                var name = Path.GetFileName(childFullPath);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var attrs = File.GetAttributes(childFullPath);
                var isSymlink = (attrs & FileAttributes.ReparsePoint) != 0;
                bool isDir;
                if (isSymlink)
                {
                    try
                    {
                        isDir = Directory.Exists(childFullPath);
                    }
                    catch
                    {
                        isDir = (attrs & FileAttributes.Directory) != 0;
                    }
                }
                else
                {
                    isDir = (attrs & FileAttributes.Directory) != 0;
                }

                entries.Add(new HostDirEntryDto(name, isDir, isSymlink));
            }
            catch (UnauthorizedAccessException)
            {
                // Skip inaccessible child entry (e.g. FUSE mount or root-only dir) without failing parent listing.
            }
            catch (IOException)
            {
                // Skip transiently vanishing or special child entry.
            }
        }

        entries.Sort(static (a, b) =>
        {
            if (a.IsDir != b.IsDir)
            {
                return a.IsDir ? -1 : 1;
            }

            var cmp = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });

        var parent = Directory.GetParent(fullPath)?.FullName;
        return new HostListDirResultDto(fullPath, parent, entries);
    }

    private async Task<IReadOnlyList<ProviderStatus>> GetCachedOrProbeProvidersAsync(
        bool forceRefresh,
        CancellationToken ct)
    {
        var nowMs = NowMs();
        if (!forceRefresh &&
            _cachedProviders is { } cached &&
            (nowMs - Volatile.Read(ref _cachedProvidersAtMs)) < ProviderProbeCacheTtlMs)
        {
            return cached;
        }

        await _providerProbeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            nowMs = NowMs();
            if (!forceRefresh &&
                _cachedProviders is { } cachedInside &&
                (nowMs - _cachedProvidersAtMs) < ProviderProbeCacheTtlMs)
            {
                return cachedInside;
            }

            IReadOnlyList<ProviderStatus> probed;
            if (forceRefresh)
            {
                var forceOverload = _agentRuntime
                    .GetType()
                    .GetMethod("ProbeProvidersAsync", [typeof(bool), typeof(CancellationToken)]);
                if (forceOverload is not null &&
                    forceOverload.Invoke(_agentRuntime, [true, ct]) is Task<IReadOnlyList<ProviderStatus>> forceTask)
                {
                    probed = await forceTask.ConfigureAwait(false);
                }
                else
                {
                    probed = await _agentRuntime.ProbeProvidersAsync(ct).ConfigureAwait(false);
                }
            }
            else
            {
                probed = await _agentRuntime.ProbeProvidersAsync(ct).ConfigureAwait(false);
            }

            _cachedProviders = probed;
            Volatile.Write(ref _cachedProvidersAtMs, NowMs());
            return probed;
        }
        finally
        {
            _providerProbeLock.Release();
        }
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

        var evt = new EventEnvelopeDto(null, area.Id, null, KernelEventKinds.AreaUpdated, JsonHelpers.ToElement(area), now);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var id = await _store.CreateAreaAsync(area, key, ct, evt).ConfigureAwait(false);
        if (id == area.Id)
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None, allowFallback: false).ConfigureAwait(false);
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

        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var persisted = await _store.UpsertAreaAsync(updated, ct).ConfigureAwait(false);
        var evt = new EventEnvelopeDto(
            EventId: null,
            AreaId: persisted.Id,
            TrackId: null,
            Kind: KernelEventKinds.AreaUpdated,
            Payload: JsonHelpers.ToElement(persisted),
            CreatedAtMs: now);
        await _store.AppendEventAsync(evt, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct, allowFallback: false).ConfigureAwait(false);
        return persisted;
    }

    public async Task DeleteAreaAsync(string areaId, CancellationToken ct = default)
    {
        _ = await _store.GetAreaAsync(areaId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Area '{areaId}' not found.");

        // Cleanly tear down child tracks so workspaces, processes, terminals, sessions, and actors do not leak.
        var childTracks = await _store.ListTracksAsync(areaId, includeClosed: true, ct).ConfigureAwait(false);
        foreach (var track in childTracks)
        {
            await DeleteTrackAsync(track.Id, ct).ConfigureAwait(false);
        }

        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.DeleteAreaAsync(areaId, ct, requireEmpty: true).ConfigureAwait(false);
        var evt = new EventEnvelopeDto(null, areaId, null, KernelEventKinds.AreaDeleted,
            JsonHelpers.ToElement(new { id = areaId }), NowMs());
        await _store.AppendEventAsync(evt, CancellationToken.None).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None, allowFallback: false).ConfigureAwait(false);
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

        var source = WorkspaceSource(area, req.Cwd, useExplicitEmpty: req.WorkspaceOptionId is not null);
        WorkspaceChoice? choice = null;
        if (req.WorkspaceOptionId is { } optionId)
        {
            if (!string.IsNullOrWhiteSpace(req.WorkspaceDriver))
                throw KernelException.InvalidArgument("Choose a workspace option or specify a workspace driver.");
            var choices = await WorkspaceChoicesAsync(source, policy?.Manifest.WorkspaceDriver, ct).ConfigureAwait(false);
            choice = choices.FirstOrDefault(candidate => candidate.Option.Id == optionId)
                ?? throw KernelException.InvalidArgument("The selected workspace option is unavailable for this folder. Choose an available workspace option.");
        }
        var driverId = choice?.DriverId ?? PreferredWorkspaceDriver(req.WorkspaceDriver, policy?.Manifest.WorkspaceDriver);
        var blankWorkspace = source is null;
        var sourcePath = blankWorkspace
            ? Path.Combine(_options.WorkspacesRootDirectory, trackId)
            : source!;
        var targetRootDir = Path.Combine(_options.WorkspacesRootDirectory, blankWorkspace ? "_worktrees" : trackId);
        var lowerCacheDirs = policy?.Manifest.WorkspaceLowerCacheDirs ?? [];
        var createCommand = policy?.Manifest.WorkspaceCreateCmd;
        if (blankWorkspace && (driverId == VcsWorkspaceDriver.Id || policy?.Manifest.WorkspaceDriver == VcsWorkspaceDriver.Id))
            createCommand = string.IsNullOrWhiteSpace(createCommand) ? "git init -q" : $"git init -q && (\n{createCommand}\n)";

        var workspaceDriver = _workspaceDrivers.GetRequired(driverId);
        if (choice is null && !await workspaceDriver.IsAvailableAsync(source, ct).ConfigureAwait(false))
            throw KernelException.PreconditionFailed($"Workspace driver '{driverId}' is unavailable for this source on this Host.");
        var preparation = new WorkspacePreparation(
            new WorkspaceProvisionRequest(trackId, Path.GetFullPath(sourcePath), Path.GetFullPath(targetRootDir),
                lowerCacheDirs, req.BaseRevision, NewEmptyWorkspace: blankWorkspace, VcsBackend: choice?.Backend),
            createCommand, RepeatableProvision: workspaceDriver.ProvisionIsRepeatable);

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
            Provider: string.IsNullOrWhiteSpace(req.Provider) ? _options.DefaultProvider
                : TaskBlockSynchronizer.NormalizeProviderId(req.Provider) ?? req.Provider.Trim(),
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
        var (rootFull, candidateFull, sanitizedRel) = ResolveContainedWorkspacePathOrThrow(mounted.MergedCwd, relativePath);

        if (Directory.Exists(candidateFull))
        {
            var containedDirectory = EnsureRealPathContainedInWorkspace(rootFull, candidateFull);
            using var openedDirectory = UseWorkspaceFileReader(() => WorkspaceFileReader.OpenDirectory(containedDirectory));
            var directoryPath = openedDirectory is null ? containedDirectory
                : $"/proc/self/fd/{openedDirectory.DangerousGetHandle()}";
            var entries = new List<WorkspaceFileEntryDto>();
            foreach (var dir in Directory.GetDirectories(directoryPath).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                var rel = Path.GetRelativePath(rootFull, Path.Combine(candidateFull, name)).Replace('\\', '/');
                entries.Add(new WorkspaceFileEntryDto(name, rel, IsDirectory: true, SizeBytes: 0));
            }

            foreach (var file in Directory.GetFiles(directoryPath).OrderBy(f => f, StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                var rel = Path.GetRelativePath(rootFull, Path.Combine(candidateFull, info.Name)).Replace('\\', '/');
                entries.Add(new WorkspaceFileEntryDto(info.Name, rel, IsDirectory: false, SizeBytes: info.Length));
            }

            var relDir = string.Equals(candidateFull, rootFull, StringComparison.Ordinal)
                ? string.Empty
                : Path.GetRelativePath(rootFull, candidateFull).Replace('\\', '/');
            return new WorkspaceFileResultDto(
                relDir,
                IsDirectory: true,
                Content: null,
                Truncated: false,
                Entries: entries,
                IsBinary: false);
        }

        if (!File.Exists(candidateFull))
        {
            throw KernelException.NotFound($"Workspace path '{sanitizedRel}' not found.");
        }

        var canonicalPath = EnsureRealPathContainedInWorkspace(rootFull, candidateFull);
        await using var fs = UseWorkspaceFileReader(() => WorkspaceFileReader.Open(canonicalPath));
        var limit = Math.Max(1, maxBytes);
        var buffer = new byte[Math.Min(fs.Length, limit)];
        var read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        var relFile = Path.GetRelativePath(rootFull, candidateFull).Replace('\\', '/');

        var sniffLen = Math.Min(read, BinarySniffBytes);
        if (buffer.AsSpan(0, sniffLen).IndexOf((byte)0) >= 0)
        {
            return new WorkspaceFileResultDto(
                relFile,
                IsDirectory: false,
                Content: null,
                Truncated: false,
                Entries: null,
                IsBinary: true);
        }

        var truncated = fs.Length > read;
        var validLen = truncated ? TrimTrailingIncompleteUtf8(buffer.AsSpan(0, read)) : read;
        var text = Encoding.UTF8.GetString(buffer, 0, validLen);

        return new WorkspaceFileResultDto(
            relFile,
            IsDirectory: false,
            Content: text,
            Truncated: truncated,
            Entries: null,
            IsBinary: false);
    }

    public async Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(
        string trackId,
        string? relativePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw KernelException.InvalidArgument("Query parameter 'path' is required.");
        }

        var mounted = await Actors.GetOrActivate(trackId).EnsureMountedAsync(ct).ConfigureAwait(false);
        var (rootFull, candidateFull, sanitizedRel) = ResolveContainedWorkspacePathOrThrow(mounted.MergedCwd, relativePath);

        if (Directory.Exists(candidateFull))
        {
            throw KernelException.NotFound($"Workspace path '{sanitizedRel}' is a directory, not a file.");
        }

        if (!File.Exists(candidateFull))
        {
            throw KernelException.NotFound($"Workspace file '{sanitizedRel}' not found.");
        }

        var canonicalPath = EnsureRealPathContainedInWorkspace(rootFull, candidateFull);
        return (canonicalPath, UseWorkspaceFileReader(() => WorkspaceFileReader.Open(canonicalPath)));
    }

    private static (string RootFull, string CandidateFull, string SanitizedRel) ResolveContainedWorkspacePathOrThrow(
        string workspaceRoot,
        string? rawPath)
    {
        var rootFull = Path.GetFullPath(workspaceRoot);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return (rootFull, rootFull, string.Empty);
        }

        var trimmed = rawPath.Trim();
        string candidateFull;
        if (Path.IsPathRooted(trimmed))
        {
            var rootedFull = Path.GetFullPath(trimmed);
            if (string.Equals(rootedFull, rootFull, StringComparison.Ordinal) ||
                rootedFull.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
                candidateFull = rootedFull;
            }
            else
            {
                candidateFull = Path.GetFullPath(Path.Combine(rootFull, trimmed.TrimStart('/', '\\')));
            }
        }
        else
        {
            candidateFull = Path.GetFullPath(Path.Combine(rootFull, trimmed));
        }

        if (!string.Equals(candidateFull, rootFull, StringComparison.Ordinal) &&
            !candidateFull.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw KernelException.Forbidden("Path escapes workspace root.");
        }

        var sanitizedRel = string.Equals(candidateFull, rootFull, StringComparison.Ordinal)
            ? string.Empty
            : Path.GetRelativePath(rootFull, candidateFull).Replace('\\', '/');
        return (rootFull, candidateFull, sanitizedRel);
    }

    private static string EnsureRealPathContainedInWorkspace(string rootFull, string candidateFull) =>
        UseWorkspaceFileReader(() => WorkspaceFileReader.ResolveContainedPath(rootFull, candidateFull));

    private static T UseWorkspaceFileReader<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (FileNotFoundException ex)
        {
            throw KernelException.NotFound(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw KernelException.Forbidden(ex.Message);
        }
    }

    private static int TrimTrailingIncompleteUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0;
        }

        var i = bytes.Length - 1;
        var cont = 0;
        while (i >= 0 && cont < 4 && (bytes[i] & 0b1100_0000) == 0b1000_0000)
        {
            i--;
            cont++;
        }

        if (i < 0)
        {
            return bytes.Length;
        }

        var lead = bytes[i];
        var expected = lead switch
        {
            < 0b1000_0000 => 1,
            >= 0b1111_0000 => 4,
            >= 0b1110_0000 => 3,
            >= 0b1100_0000 => 2,
            _ => 1
        };

        return (bytes.Length - i) < expected ? i : bytes.Length;
    }

    public Task<DiffSummary> GetTrackVcsDiffAsync(
        string trackId,
        string? fromRev = null,
        string? toRev = null,
        CancellationToken ct = default) =>
        GetTrackVcsDiffAsync(trackId, fromRev, toRev, path: null, ct);

    public async Task<DiffSummary> GetTrackVcsDiffAsync(
        string trackId,
        string? fromRev,
        string? toRev,
        string? path,
        CancellationToken ct = default)
    {
        var mounted = await Actors.GetOrActivate(trackId).EnsureMountedAsync(ct).ConfigureAwait(false);
        string? normalizedRelPath = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var (_, _, sanitizedRel) = ResolveContainedWorkspacePathOrThrow(mounted.MergedCwd, path);
            normalizedRelPath = string.IsNullOrEmpty(sanitizedRel) ? null : sanitizedRel;
        }

        return await _vcsService
            .DiffDetailedAsync(mounted.MergedCwd, fromRev, toRev, normalizedRelPath, ct, searchAncestors: !mounted.ManagedEmptyWorkspace)
            .ConfigureAwait(false);
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

    public Task<WorkerDispatchReceipt> DispatchWorkerAsync(string trackId, DispatchWorkerRequest request, string? requestKey,
        McpSessionPrincipal? caller, CancellationToken ct) =>
        Actors.GetOrActivate(caller?.TrackId ?? trackId).DispatchWorkerAsync(request, requestKey, caller?.SessionId, ct);

    public Task<IReadOnlyList<TaskAttemptDto>> ListTaskAttemptsAsync(
        string trackId,
        string taskKeyOrId,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ListTaskAttemptsAsync(taskKeyOrId, ct);

    public Task<TaskDto> CancelTaskAsync(
        string trackId,
        string? callerSessionId,
        string taskKeyOrId,
        string? reason = null,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CancelTaskAsync(callerSessionId, taskKeyOrId, reason, ct);

    public Task<TaskDto> AcceptTaskAsync(
        string trackId,
        string? callerSessionId,
        string taskKeyOrId,
        string? note = null,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).AcceptTaskAsync(callerSessionId, taskKeyOrId, note, ct);

    public Task<TaskDto> RejectTaskAsync(
        string trackId,
        string? callerSessionId,
        string taskKeyOrId,
        string note,
        bool reQueue = false,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).RejectTaskAsync(callerSessionId, taskKeyOrId, note, reQueue, ct);

    public Task<TaskDto> CompleteAttemptDoneAsync(
        string trackId,
        string callerSessionId,
        JsonElement? workerResult = null,
        CancellationToken ct = default, WorkerCompletionOrigin? origin = null) =>
        Actors.GetOrActivate(trackId).CompleteAttemptDoneAsync(callerSessionId, workerResult, ct, origin);

    public Task<TaskDto> CompleteAttemptFailAsync(
        string trackId,
        string callerSessionId,
        string reason,
        JsonElement? workerResult = null,
        CancellationToken ct = default, WorkerCompletionOrigin? origin = null) =>
        Actors.GetOrActivate(trackId).CompleteAttemptFailAsync(callerSessionId, reason, workerResult, ct, origin);

    // ------------------- Sessions, Messages & Epoch Compaction -------------------

    public Task<IReadOnlyList<AgentSessionDto>> ListSessionsAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ListSessionsAsync(ct);

    public Task<AgentSessionDto> CreateSubagentSessionAsync(
        string trackId,
        CreateSessionRequest req,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CreateSubagentSessionAsync(req, ct);

    public Task<(SessionCreationReceipt Receipt, bool Replay)> CreateSessionWithReceiptAsync(
        string trackId,
        CreateSessionRequest req,
        string key,
        CancellationToken ct) =>
        Actors.GetOrActivate(trackId).CreateSessionWithReceiptAsync(req, key, ct);

    public async Task<AgentSessionDto> PatchSessionAsync(
        string sessionId,
        PatchSessionRequest req,
        CancellationToken ct = default)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).PatchSessionAsync(sessionId, req, ct).ConfigureAwait(false);
    }

    public async Task<MessageAcceptance> AcceptMessageAsync(
        string sessionId, SendMessageRequest req, CancellationToken ct)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).AcceptMessageAsync(sessionId, req, ct).ConfigureAwait(false);
    }

    public async Task<AgentSessionDto> ResumeProviderSessionAsync(string sessionId, string turnId, CancellationToken ct)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).ResumeProviderSessionAsync(sessionId, turnId, ct).ConfigureAwait(false);
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
        CancellationToken ct = default, bool latest = false, long? beforeId = null, McpSessionPrincipal? caller = null,
        string? activityTurnId = null, long? afterSequence = null, int waitMs = 0)
    {
        var trackId = caller?.TrackId ?? await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        var actor = Actors.GetOrActivate(trackId);
        Task<SessionItemsPageDto> ReadAsync() => actor.GetSessionItemsAsync(sessionId, afterId, limit, ct, latest, beforeId,
            caller?.SessionId, activityTurnId, afterSequence);
        if (waitMs <= 0 || afterSequence is null || activityTurnId is null)
            return await ReadAsync().ConfigureAwait(false);

        await using var events = EventBus.Subscribe(trackId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Min(waitMs, 10_000));
        var page = await ReadAsync().ConfigureAwait(false);
        while (page is { Worker: { Reset: false } worker } && page.ActiveTurn?.Id == activityTurnId && worker.Sequence <= afterSequence)
        {
            EventEnvelopeDto update;
            try
            {
                update = await events.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return await ReadAsync().ConfigureAwait(false);
            }

            if (update.Kind is KernelEventKinds.AskChanged or KernelEventKinds.SessionUpdated or KernelEventKinds.TurnCompleted ||
                JsonHelpers.TryGetPropertyIgnoreCase(update.Payload, ["sessionId"], out var target) && target.ValueKind == JsonValueKind.String && target.GetString() == sessionId)
                page = await ReadAsync().ConfigureAwait(false);
        }
        return page;
    }

    public async Task<string?> GetTrackLeadSessionIdAsync(string trackId, CancellationToken ct) =>
        (await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false))?.LeadSessionId;

    public async Task<WorkerMessageReceipt> SendWorkerMessageAsync(
        string sessionId, WorkerMessageRequest request, McpSessionPrincipal? caller, CancellationToken ct)
    {
        var trackId = caller?.TrackId ?? await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).SendWorkerMessageAsync(sessionId, request, caller?.SessionId, ct).ConfigureAwait(false);
    }

    public async Task InterruptWorkerAsync(string sessionId, string attemptId, string turnId, McpSessionPrincipal? caller, CancellationToken ct)
    {
        var trackId = caller?.TrackId ?? await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        await Actors.GetOrActivate(trackId).InterruptWorkerAsync(sessionId, attemptId, turnId, caller?.SessionId, ct).ConfigureAwait(false);
    }

    public async Task<WorkerSettings> UpdateWorkerSettingsAsync(string sessionId, UpdateWorkerSettingsRequest request, McpSessionPrincipal? caller,
        CancellationToken ct, bool verifiedApprovalOwner = false)
    {
        var trackId = caller?.TrackId ?? await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).UpdateWorkerSettingsAsync(sessionId, request, caller?.SessionId, ct, verifiedApprovalOwner).ConfigureAwait(false);
    }

    public async Task<SteeringResult> SteerQueuedMessageAsync(string sessionId, string inputId, string turnId, CancellationToken ct,
        McpSessionPrincipal? caller = null, string? attemptId = null)
    {
        var trackId = caller?.TrackId ?? await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).SteerQueuedMessageAsync(sessionId, inputId, turnId, ct, caller?.SessionId, attemptId).ConfigureAwait(false);
    }

    public async Task RemoveQueuedMessageAsync(string sessionId, string inputId, CancellationToken ct,
        McpSessionPrincipal? caller = null, string? attemptId = null)
    {
        var trackId = caller?.TrackId ?? await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        await Actors.GetOrActivate(trackId).RemoveQueuedMessageAsync(sessionId, inputId, ct, caller?.SessionId, attemptId).ConfigureAwait(false);
    }

    public async Task<SteeringResult> SteerMessageAsync(string sessionId, string turnId, string content, CancellationToken ct, string? requestKey = null)
    {
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await Actors.GetOrActivate(trackId).SteerMessageAsync(sessionId, turnId, content, ct, requestKey).ConfigureAwait(false);
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
        _providerProbeLock.Dispose();
    }
}
