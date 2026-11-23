using System.Collections.Concurrent;
using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Actors;
using Perga.Kernel.Boot;
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

namespace Perga.Kernel;

public partial interface IPergaKernel : IAsyncDisposable
{
    IKernelEventBus EventBus { get; }
    ITemplateCatalog Templates { get; }
    IMcpSessionBridge McpSessionBridge { get; }
    IExecutionEnvironmentRegistry ExecutionEnvironments { get; }
    TrackActorRegistry Actors { get; }
    IBootReconciler BootReconciler { get; }
    SystemUpgrade.DispatchAdmission DispatchAdmission { get; }
    SystemUpgrade.UpgradeReceiptConsumer UpgradeReceipts { get; }
    Task<JsonElement> InvokeSystemUpgradeAsync(McpSessionPrincipal caller, string operation, JsonElement arguments, CancellationToken ct);

    // System & Bootstrap
    Task<BootstrapDto> GetBootstrapAsync(CancellationToken ct = default);
    Task<BootstrapDto> GetBootstrapAsync(string? scope, bool refresh = false, CancellationToken ct = default, string? card_id = null);
    Task<IReadOnlyList<ProviderStatus>> GetTargetProvidersAsync(string targetId, bool refresh = false, CancellationToken ct = default);
    Task<TrackProvidersDto> ListProvidersForTrackAsync(string trackId, string? provider = null,
        bool refresh = false, bool includeUnavailable = false, CancellationToken ct = default);
    Task<TrackOverlaysResponseDto> GetOverlaysAsync(bool includeClosed = false, CancellationToken ct = default);
    Task<HostListDirResultDto> ListHostDirectoryAsync(string? path = null, string? targetId = null, CancellationToken ct = default);
    IReadOnlyList<CompiledTemplateManifest> ListTemplates();
    JsonElement InspectTemplate(string? templateId);
    Task<JsonElement> InspectCurrentTemplateAsync(string trackId, string sessionId, CancellationToken ct = default);
    Task<TemplateSetResult> SetTrackTemplateWithOptionsAsync(string trackId, string sessionId, string? templateId,
        string? source, string generation, JsonElement? inputs, bool dryRun, bool clear, CancellationToken ct = default, bool resetData = false);
    Task<TrackDto> UpdateTemplateInputsAsync(string trackId, string sessionId, JsonElement inputs, string generation, CancellationToken ct = default);
    Task<TrackDto> SetTrackTemplateAsync(string trackId, string callerSessionId, string? templateId, string? source, string expectedPolicyGeneration, JsonElement? inputs = null, CancellationToken ct = default, bool resetData = false);
    Task<BootReconcileResult> ReconcileOnBootAsync(CancellationToken ct = default);
    Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(CancellationToken ct = default);

    Task<DailyTrackDto?> GetDailyTrackAsync(string? date, CancellationToken ct = default);

    // Areas
    Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct = default);
    Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct = default);
    Task<AreaDto> CreateAreaAsync(CreateAreaRequest req, CancellationToken ct = default, string? requestKey = null);
    Task<AreaDto> PatchAreaAsync(string areaId, PatchAreaRequest req, CancellationToken ct = default);
    Task DeleteAreaAsync(string areaId, CancellationToken ct = default);

    // Tracks
    Task<IReadOnlyList<TrackDto>> ListTracksAsync(string? areaId = null, bool includeClosed = false, CancellationToken ct = default);
    Task<TrackAggregateSnapshot> CreateTrackAsync(CreateTrackRequest req, CancellationToken ct = default, string? requestKey = null);
    Task<TrackWorkspaceOptionsDto> GetTrackWorkspaceOptionsAsync(string areaId, string? cwd, string? templateId, CancellationToken ct, string? targetId = null);
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
    Task<TemplateViewsProjection> GetTrackViewsAsync(string trackId, CancellationToken ct = default);
    Task<FlowSnapshotDto> GetTrackFlowAsync(string trackId, CancellationToken ct = default);
    Task<FlowCommitResultDto> CommitTrackFlowAsync(string trackId, bool dryRun, long? expectedFlowRev, InteropPrincipal caller, CancellationToken ct = default, string? content = null);
    Task<TrackDataPatchResult> PatchTrackDataAsync(string trackId, JsonElement patch, string source, string? callerSessionId = null, CancellationToken ct = default, long? expectedDataRev = null);
    Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(string trackId, int docRev, CancellationToken ct = default);
    Task<ReportReadResult> CommitReportAsync(string trackId, string callerSessionId, ReportCommitRequest req,
        CancellationToken ct = default, McpExecutionOrigin? origin = null, string? expectedPolicyGeneration = null);

    // Tasks, Governance & Completion
    Task<IReadOnlyList<TaskDto>> ListTasksAsync(string trackId, CancellationToken ct = default);
    Task<TaskDto> CancelTaskAsync(string trackId, string? callerSessionId, string taskKeyOrId, string? reason = null, CancellationToken ct = default);
    Task<TaskDto> CompleteTaskDoneAsync(string trackId, string callerSessionId, JsonElement? workerResult = null, CancellationToken ct = default, WorkerCompletionOrigin? origin = null, FlowOutcome outcome = FlowOutcome.Ok, string? summary = null);
    Task<TaskDto> CompleteTaskFailAsync(string trackId, string callerSessionId, string reason, JsonElement? workerResult = null, CancellationToken ct = default, WorkerCompletionOrigin? origin = null);

    // Sessions, Messages & Epoch Compaction
    AttachmentUploadLimits AttachmentLimits { get; }
    Task<StoredSessionAttachment> UploadSessionAttachmentAsync(string sessionId, Stream content, string? originalFilename, CancellationToken ct = default);
    Task<StoredSessionAttachment?> GetSessionAttachmentAsync(string sessionId, string attachmentId, CancellationToken ct = default);
    Task<StoredSessionAttachment> UploadStagedAttachmentAsync(string? trackId, Stream content, string? originalFilename, CancellationToken ct = default);
    Task<StoredSessionAttachment?> GetUnownedStagedAttachmentAsync(string attachmentId, CancellationToken ct = default);
    Task<Stream?> OpenAttachmentStreamAsync(StoredSessionAttachment attachment, CancellationToken ct = default);
    Task<ReadReceiptsResponseDto> GetReadReceiptsAsync(CancellationToken ct = default);
    Task<ReadReceiptDto> MarkReadAsync(string kind, string targetId, long readThroughMs, CancellationToken ct = default);
    Task<ReadReceiptDto> MarkUnreadAsync(string kind, string targetId, CancellationToken ct = default);
    // Asks
    Task<JsonElement> AskUserAsync(string trackId, string? callerSessionId, IReadOnlyList<AskQuestionDto> questions, CancellationToken ct = default);


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
    Task<ITerminalBridgeClient> ResolveTerminalClientAsync(string terminalId, CancellationToken ct = default);

    // MCP Policy Guard, Tool Dispatch & Schedules
    McpSessionPrincipal ObserveMcpExecution(McpSessionPrincipal caller);
    Task<string> GuardMcpToolCallAsync(string trackId, string sessionId, string mcpServer, string toolName, JsonElement arguments,
        CancellationToken ct = default, McpExecutionOrigin? origin = null);
    Task<JsonElement> InvokeKernelMcpToolAsync(McpSessionPrincipal caller, string toolName, JsonElement arguments, CancellationToken ct = default);
    Task<bool> CancelPendingWakeAsync(string trackId, string wakeId, CancellationToken ct = default);
    Task<IReadOnlyList<FiredTrackSchedule>> FireDueTrackSchedulesAsync(long nowMs, CancellationToken ct = default);
    Task<TodayCalendarDto> GetTodayCalendarAsync(string startDate, string endDate, CancellationToken ct = default);

    // Synchronization helper for tests / shutdown
    Task WaitForTrackIdleAsync(string trackId, CancellationToken ct = default);
}

public sealed partial class PergaKernelService : IPergaKernel
{
    private const long ProviderProbeCacheTtlMs = 60_000L;
    private static readonly string ProcessDbInstanceId = Guid.NewGuid().ToString("N");

    private readonly ISystemUpgradeController? _systemUpgradeController;
    private readonly IScheduleCronEvaluator? _scheduleCronEvaluator;
    private readonly ITrackStore _store;
    private readonly IReportDocEngine _reportEngine;
    private readonly IPolicyVm _policyVm;
    private readonly IExecutionEnvironmentRegistry _executionEnvironments;
    private readonly EventHub _eventHub;
    private readonly KernelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly string _fallbackDatabaseId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _providerProbeLocks = new(StringComparer.Ordinal);

    private sealed record ProviderObservation(IReadOnlyList<ProviderStatus> Providers, long ObservedAtMs);
    private readonly ConcurrentDictionary<string, ProviderObservation> _cachedProvidersByTarget = new(StringComparer.Ordinal);
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
        TimeProvider? timeProvider = null,
        ISystemUpgradeController? systemUpgradeController = null,
        ISystemUpgradeReceiptSource? upgradeReceiptSource = null,
        IScheduleCronEvaluator? scheduleCronEvaluator = null,
        IExecutionEnvironmentRegistry? executionEnvironments = null,
        IGitHubForgeService? gitHubForgeService = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reportEngine = reportEngine ?? throw new ArgumentNullException(nameof(reportEngine));
        _policyVm = policyVm ?? throw new ArgumentNullException(nameof(policyVm));
        ArgumentNullException.ThrowIfNull(agentRuntime);
        ArgumentNullException.ThrowIfNull(workspaceDrivers);
        ArgumentNullException.ThrowIfNull(vcsService);
        ArgumentNullException.ThrowIfNull(terminalClient);
        _options = options ?? new KernelOptions();
        _systemUpgradeController = systemUpgradeController;
        _scheduleCronEvaluator = scheduleCronEvaluator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var resolvedCommandRunner = commandRunner ?? new ProcessShellCommandRunner();
        _eventHub = eventHub ?? new EventHub();

        _executionEnvironments = executionEnvironments ?? new ExecutionEnvironmentRegistry(
        [
            new LocalExecutionEnvironment(
                workspacesRoot: _options.WorkspacesRootDirectory,
                agentRuntime: agentRuntime,
                shell: resolvedCommandRunner,
                vcs: vcsService,
                workspaceDrivers: workspaceDrivers,
                terminals: terminalClient,
                forge: gitHubForgeService)
        ]);

        Templates = templateCatalog ?? new TemplateCatalog(_policyVm, _options.TemplatesDirectory);
        McpSessionBridge = mcpSessionBridge ?? new InMemoryMcpSessionBridge(_options.McpBaseUri);
        Actors = new TrackActorRegistry(
            _store,
            _reportEngine,
            _policyVm,
            Templates,
            _executionEnvironments,
            McpSessionBridge,
            _eventHub,
            _options,
            _timeProvider,
            _scheduleCronEvaluator);
        UpgradeReceipts = new SystemUpgrade.UpgradeReceiptConsumer(_store, upgradeReceiptSource, Actors, _eventHub, DispatchAdmission);
        BootReconciler = new BootReconciler(_store, workspaceDrivers, Actors, UpgradeReceipts);
        DispatchAdmission.Changed += PublishUpgradeStatus;
        DispatchAdmission.Resumed += ResumeSchedulers;
    }

    public IKernelEventBus EventBus => _eventHub;
    public ITemplateCatalog Templates { get; }
    public IMcpSessionBridge McpSessionBridge { get; }
    public IExecutionEnvironmentRegistry ExecutionEnvironments => _executionEnvironments;
    public TrackActorRegistry Actors { get; }
    public IBootReconciler BootReconciler { get; }
    public SystemUpgrade.DispatchAdmission DispatchAdmission => _options.DispatchAdmission;
    public SystemUpgrade.UpgradeReceiptConsumer UpgradeReceipts { get; }
    public AttachmentUploadLimits AttachmentLimits => _store.SessionAttachments?.Limits ?? new AttachmentUploadLimits();

    public async Task<StoredSessionAttachment> UploadSessionAttachmentAsync(
        string sessionId, Stream content, string? originalFilename, CancellationToken ct = default)
    {
        var attachments = RequireAttachmentStore();
        var trackId = await ResolveTrackIdForSessionAsync(sessionId, ct).ConfigureAwait(false);
        return await attachments.StoreStagedAsync(trackId, sessionId, content, originalFilename, ct).ConfigureAwait(false);
    }

    public Task<StoredSessionAttachment?> GetSessionAttachmentAsync(
        string sessionId, string attachmentId, CancellationToken ct = default) =>
        _store.SessionAttachments is { } attachments
            ? attachments.GetForSessionAsync(sessionId, attachmentId, ct)
            : Task.FromResult<StoredSessionAttachment?>(null);

    public async Task<StoredSessionAttachment> UploadStagedAttachmentAsync(
        string? trackId, Stream content, string? originalFilename, CancellationToken ct = default)
    {
        var attachments = RequireAttachmentStore();
        var normalizedTrackId = string.IsNullOrWhiteSpace(trackId) ? null : trackId.Trim();
        if (normalizedTrackId is not null)
        {
            var track = await _store.GetTrackAsync(normalizedTrackId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound($"Track '{normalizedTrackId}' not found.");
            if (track.ClosedAtMs is not null)
            {
                throw KernelException.PreconditionFailed("Cannot upload attachments to a closed Track.");
            }
        }

        return await attachments.StoreStagedAsync(normalizedTrackId, sessionId: null, content, originalFilename, ct).ConfigureAwait(false);
    }

    public Task<StoredSessionAttachment?> GetUnownedStagedAttachmentAsync(
        string attachmentId, CancellationToken ct = default) =>
        _store.SessionAttachments is { } attachments
            ? attachments.GetUnownedStagedAsync(attachmentId, ct)
            : Task.FromResult<StoredSessionAttachment?>(null);

    public Task<Stream?> OpenAttachmentStreamAsync(StoredSessionAttachment attachment, CancellationToken ct = default) =>
        _store.SessionAttachments is { } attachments
            ? attachments.OpenReadStreamAsync(attachment, ct)
            : Task.FromResult<Stream?>(null);

    private ISessionAttachmentStore RequireAttachmentStore() =>
        _store.SessionAttachments ?? throw KernelException.PreconditionFailed("Image attachment storage is unavailable.");

    // ------------------- System & Bootstrap -------------------

    public Task<BootstrapDto> GetBootstrapAsync(CancellationToken ct = default) =>
        GetBootstrapAsync(scope: null, refresh: false, ct);

    public async Task<BootstrapDto> GetBootstrapAsync(
        string? scope,
        bool refresh = false,
        CancellationToken ct = default,
        string? card_id = null)
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
            var targetId = ExecutionTargetIds.Local;
            if (!string.IsNullOrWhiteSpace(card_id))
            {
                var trackId = await _store.FindTrackIdBySessionAsync(card_id, ct).ConfigureAwait(false);
                var track = trackId is null ? null : await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false);
                targetId = track?.ExecutionTargetId ?? ExecutionTargetIds.Local;
            }

            var providersTask = GetCachedOrProbeProvidersAsync(targetId, refresh, ct);
            await Task.WhenAll(providersTask, areasTask).ConfigureAwait(false);
            providers = (await providersTask.ConfigureAwait(false)).Providers;
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
            AuthRequired: false,
            ExecutionTargets: _executionEnvironments.ListTargets());
    }

    public async Task<TrackOverlaysResponseDto> GetOverlaysAsync(
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        var stored = await _store.ListTrackOverlaysAsync(includeClosed, ct).ConfigureAwait(false);
        var merged = new List<TrackOverlayDto>(stored.Count);
        foreach (var item in stored)
        {
            merged.Add(await MergeActiveOverlayAsync(item,
                () => Actors.TryGetActive(item.TrackId, out var actor) ? actor : null, ct).ConfigureAwait(false));
        }

        return new TrackOverlaysResponseDto(merged);
    }

    // A registry lookup is not a lease: eviction can complete the actor's mailbox before enqueue.
    // Retry only a newer active actor, once; otherwise the durable overlay remains authoritative.
    internal static async Task<TrackOverlayDto> MergeActiveOverlayAsync(
        TrackOverlayDto stored, Func<TrackActor?> getActive, CancellationToken ct)
    {
        var actor = getActive();
        for (var attempt = 0; actor is not null && attempt < 2; attempt++)
        {
            try { return await actor.MergeOverlayAsync(stored, ct).ConfigureAwait(false); }
            catch (ObjectDisposedException ex) when (ex.ObjectName == typeof(TrackActor).FullName)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt == 1) break;
                var next = getActive();
                if (ReferenceEquals(next, actor)) break;
                actor = next;
            }
        }
        return stored;
    }

    public Task<ReadReceiptsResponseDto> GetReadReceiptsAsync(CancellationToken ct = default) =>
        _store.GetReadReceiptsAsync(ct);

    public async Task<ReadReceiptDto> MarkReadAsync(string kind, string targetId, long readThroughMs, CancellationToken ct = default)
    {
        ValidateReadReceipt(kind, targetId);
        if (readThroughMs < 0) throw KernelException.InvalidArgument("readThroughMs must be nonnegative.");
        var receipt = await _store.MarkReadAsync(kind, targetId, readThroughMs, ct).ConfigureAwait(false);
        PublishReadReceipt(receipt);
        return receipt;
    }

    public async Task<ReadReceiptDto> MarkUnreadAsync(string kind, string targetId, CancellationToken ct = default)
    {
        ValidateReadReceipt(kind, targetId);
        var receipt = await _store.MarkUnreadAsync(kind, targetId, ct).ConfigureAwait(false);
        PublishReadReceipt(receipt);
        return receipt;
    }

    private static void ValidateReadReceipt(string kind, string targetId)
    {
        if (kind is not ("track" or "conversation") || string.IsNullOrWhiteSpace(targetId))
            throw KernelException.InvalidArgument("A track or conversation kind and nonempty targetId are required.");
    }

    private void PublishReadReceipt(ReadReceiptDto receipt) =>
        _eventHub.PublishTransient(null, receipt.Kind == "track" ? receipt.TargetId : null,
            KernelEventKinds.ReadReceiptUpdated, JsonHelpers.ToElement(receipt), NowMs());

    public Task<HostListDirResultDto> ListHostDirectoryAsync(
        string? path = null,
        string? targetId = null,
        CancellationToken ct = default)
    {
        var env = _executionEnvironments.GetRequired(ExecutionTargetIds.Normalize(targetId));
        return env.Files.ListDirectoryAsync(path, ct);
    }

    private async Task<ProviderObservation> GetCachedOrProbeProvidersAsync(
        string targetId,
        bool forceRefresh,
        CancellationToken ct)
    {
        var env = _executionEnvironments.GetRequired(targetId);
        var cacheKey = env.Descriptor.Id;
        var nowMs = NowMs();
        if (!forceRefresh &&
            _cachedProvidersByTarget.TryGetValue(cacheKey, out var cached) &&
            (nowMs - cached.ObservedAtMs) < ProviderProbeCacheTtlMs)
        {
            return cached;
        }

        var probeLock = _providerProbeLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await probeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            nowMs = NowMs();
            if (!forceRefresh &&
                _cachedProvidersByTarget.TryGetValue(cacheKey, out var cachedInside) &&
                (nowMs - cachedInside.ObservedAtMs) < ProviderProbeCacheTtlMs)
            {
                return cachedInside;
            }

            IReadOnlyList<ProviderStatus> probed;
            var runtime = env.AgentRuntime;
            if (forceRefresh)
            {
                var forceOverload = runtime
                    .GetType()
                    .GetMethod("ProbeProvidersAsync", [typeof(bool), typeof(CancellationToken)]);
                if (forceOverload is not null &&
                    forceOverload.Invoke(runtime, [true, ct]) is Task<IReadOnlyList<ProviderStatus>> forceTask)
                {
                    probed = await forceTask.ConfigureAwait(false);
                }
                else
                {
                    probed = await runtime.ProbeProvidersAsync(ct).ConfigureAwait(false);
                }
            }
            else
            {
                probed = await runtime.ProbeProvidersAsync(ct).ConfigureAwait(false);
            }

            var observation = new ProviderObservation(probed, NowMs());
            _cachedProvidersByTarget[cacheKey] = observation;
            return observation;
        }
        finally
        {
            probeLock.Release();
        }
    }

    public IReadOnlyList<CompiledTemplateManifest> ListTemplates() => Templates.ListManifests();

    public JsonElement InspectTemplate(string? templateId)
    {
        if (templateId is null or "__contract__")
        {
            var path = _options.TemplatesDirectory is { } directory
                ? Path.Combine(directory, "perga-template.d.ts") : null;
            string contract;
            if (path is not null && File.Exists(path))
                contract = File.ReadAllText(path);
            else
            {
                using var stream = typeof(PergaKernelService).Assembly.GetManifestResourceStream("Perga.TemplateContract")!;
                using var reader = new StreamReader(stream);
                contract = reader.ReadToEnd();
            }
            return JsonHelpers.ToElement(new { contract });
        }
        var policy = Templates.GetRequiredPolicy(templateId);
        var source = TemplateHost.InspectSource(policy.JsSource);
        var reapply = TemplateHost.LegacyReapplySource(policy);
        return JsonHelpers.ToElement(new { policy.Manifest, JsSource = reapply ?? source, EditableSource = reapply is null ? null : source });
    }

    public Task<JsonElement> InspectCurrentTemplateAsync(string trackId, string sessionId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).InspectCurrentTemplateAsync(sessionId, ct);

    public Task<TemplateSetResult> SetTrackTemplateWithOptionsAsync(string trackId, string sessionId, string? templateId,
        string? source, string generation, JsonElement? inputs, bool dryRun, bool clear, CancellationToken ct = default, bool resetData = false) =>
        Actors.GetOrActivate(trackId).SetTemplateWithOptionsAsync(sessionId, templateId, source, generation, inputs, dryRun, clear, ct, resetData);

    public Task<TrackDto> UpdateTemplateInputsAsync(string trackId, string sessionId, JsonElement inputs, string generation, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).UpdateTemplateInputsAsync(sessionId, inputs, generation, ct);

    public Task<TrackDto> SetTrackTemplateAsync(string trackId, string callerSessionId, string? templateId,
        string? source, string expectedPolicyGeneration, JsonElement? inputs = null, CancellationToken ct = default, bool resetData = false) =>
        Actors.GetOrActivate(trackId).SetTemplateBySessionAsync(callerSessionId, templateId, source, expectedPolicyGeneration, inputs, ct, resetData);

    public Task<BootReconcileResult> ReconcileOnBootAsync(CancellationToken ct = default) =>
        BootReconciler.ReconcileAsync(ct);

    public async Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(CancellationToken ct = default)
    {
        var stats = await _store.RunMaintenancePassAsync(NowMs(), ct).ConfigureAwait(false);
        if (stats.CompactedTasks > 0)
            foreach (var track in await _store.ListTracksAsync(includeClosed: true, ct: ct).ConfigureAwait(false))
                if (Actors.TryGetActive(track.Id, out var actor))
                    await actor!.RefreshTaskDiagnosticsAsync(ct).ConfigureAwait(false);
        return stats;
    }

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

        var executionTargetId = ExecutionTargetIds.Normalize(req.ExecutionTargetId);
        if (_executionEnvironments.TryGet(executionTargetId) is null)
        {
            throw KernelException.InvalidArgument(
                $"Execution target '{executionTargetId}' is not configured on this Host.");
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
            UpdatedAtMs: now,
            ExecutionTargetId: executionTargetId);

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
        var nextExecutionTargetId = existing.ExecutionTargetId;
        if (!string.IsNullOrWhiteSpace(req.ExecutionTargetId))
        {
            var normalizedTarget = ExecutionTargetIds.Normalize(req.ExecutionTargetId);
            if (_executionEnvironments.TryGet(normalizedTarget) is null)
            {
                throw KernelException.InvalidArgument($"Unknown execution target '{normalizedTarget}'.");
            }

            nextExecutionTargetId = normalizedTarget;
        }

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(req.Name) ? existing.Name : req.Name!.Trim(),
            Color = string.IsNullOrWhiteSpace(req.Color) ? existing.Color : req.Color!,
            DefaultCwd = nextDefaultCwd,
            Folders = req.Folders?.ToArray() ?? existing.Folders,
            SortOrder = req.SortOrder ?? existing.SortOrder,
            ExecutionTargetId = nextExecutionTargetId,
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

    public Task<IReadOnlyList<TrackDto>> ListTracksAsync(string? areaId = null, bool includeClosed = false, CancellationToken ct = default) =>
        _store.ListTracksAsync(areaId, includeClosed, ct);

    public Task<TrackAggregateSnapshot> CreateTrackAsync(CreateTrackRequest req, CancellationToken ct = default, string? requestKey = null) =>
        CreateTrackCoreAsync(req, ct, requestKey);

    private async Task<TrackAggregateSnapshot> CreateTrackCoreAsync(CreateTrackRequest req, CancellationToken ct, string? requestKey, string? dailyDate = null, AreaDto? systemArea = null)
    {
        var attachmentIds = SessionAttachmentValidation.NormalizeAndValidateIds(req.AttachmentIds, AttachmentLimits.MaxPerMessage);
        req = req with { AttachmentIds = attachmentIds };
        var key = CreateKey("track", req.AreaId, requestKey, req);
        if (key is not null && await _store.FindCreationAsync(key, ct).ConfigureAwait(false) is { } replay)
            return await ReadCreatedTrackAsync(replay, ct).ConfigureAwait(false);

        var area = systemArea ?? await _store.GetAreaAsync(req.AreaId, ct).ConfigureAwait(false)
                   ?? throw KernelException.NotFound($"Area '{req.AreaId}' not found.");
        var trackTargetId = string.IsNullOrWhiteSpace(req.ExecutionTargetId)
            ? area.ExecutionTargetId
            : ExecutionTargetIds.Normalize(req.ExecutionTargetId);
        if (!string.IsNullOrWhiteSpace(req.ExecutionTargetId) && _executionEnvironments.TryGet(trackTargetId) is null)
        {
            throw KernelException.InvalidArgument($"Unknown execution target '{trackTargetId}'.");
        }

        var env = _executionEnvironments.GetRequired(trackTargetId);

        CompiledPolicy? policy = null;
        if (!string.IsNullOrWhiteSpace(req.TemplateId))
        {
            policy = Templates.GetRequiredPolicy(req.TemplateId!);
        }

        var trackId = $"trk_{Guid.NewGuid():N}";
        var leadSessionId = $"ses_{Guid.NewGuid():N}";
        var now = NowMs();

        var source = string.Equals(trackTargetId, area.ExecutionTargetId, StringComparison.Ordinal)
            ? WorkspaceSource(area, req.Cwd, useExplicitEmpty: req.WorkspaceOptionId is not null)
            : (string.IsNullOrWhiteSpace(req.Cwd) ? null : req.Cwd);
        WorkspaceChoice? choice = null;
        if (req.WorkspaceOptionId is { } optionId)
        {
            if (!string.IsNullOrWhiteSpace(req.WorkspaceDriver))
                throw KernelException.InvalidArgument("Choose a workspace option or specify a workspace driver.");
            var choices = await WorkspaceChoicesAsync(env, source, policy?.Manifest.WorkspaceDriver, ct).ConfigureAwait(false);
            choice = choices.FirstOrDefault(candidate => candidate.Option.Id == optionId)
                ?? throw KernelException.InvalidArgument("The selected workspace option is unavailable for this folder. Choose an available workspace option.");
        }
        var driverId = choice?.DriverId ?? PreferredWorkspaceDriver(req.WorkspaceDriver, policy?.Manifest.WorkspaceDriver);
        var blankWorkspace = source is null;
        var sourcePath = blankWorkspace
            ? CombineExecutionPath(env, env.WorkspacesRoot, trackId)
            : source!;
        var targetRootDir = CombineExecutionPath(env, env.WorkspacesRoot, blankWorkspace ? "_worktrees" : trackId);
        var lowerCacheDirs = policy?.Manifest.WorkspaceLowerCacheDirs ?? [];
        var createCommand = policy?.Manifest.WorkspaceCreateCmd;
        if (blankWorkspace && (driverId == VcsWorkspaceDriver.Id || policy?.Manifest.WorkspaceDriver == VcsWorkspaceDriver.Id))
            createCommand = string.IsNullOrWhiteSpace(createCommand) ? "git init -q" : $"git init -q && (\n{createCommand}\n)";

        var workspaceDriver = env.WorkspaceDrivers.GetRequired(driverId);
        if (choice is null && !await workspaceDriver.IsAvailableAsync(source, ct).ConfigureAwait(false))
            throw KernelException.PreconditionFailed($"Workspace driver '{driverId}' is unavailable for this source on target '{env.Descriptor.Id}'.");
        var preparation = new WorkspacePreparation(
            new WorkspaceProvisionRequest(trackId, NormalizeExecutionPath(env, sourcePath), NormalizeExecutionPath(env, targetRootDir),
                lowerCacheDirs, req.BaseRevision, NewEmptyWorkspace: blankWorkspace, VcsBackend: choice?.Backend),
            createCommand, RepeatableProvision: workspaceDriver.ProvisionIsRepeatable);

        var templateInputs = JsonHelpers.EnsureClonedObject(req.TemplateInputs);
        var templateState = JsonHelpers.EmptyObject;
        var templateData = JsonHelpers.EmptyObject;
        if (policy is not null)
        {
            TemplateFlow.ValidateInputs(_policyVm, policy, templateInputs);
            try { templateData = _policyVm.InitData(policy, templateInputs); }
            catch (InvalidOperationException ex) { throw KernelException.InvalidArgument(ex.Message); }
            var initEval = _policyVm.InitState(policy, templateInputs);
            templateState = initEval.Output.ValueKind != JsonValueKind.Undefined
                ? initEval.Output.Clone()
                : initEval.NextState.Clone();
        }

        var initialReport = _reportEngine.Parse(
            policy?.Manifest.InitialReportMarkdown ?? string.Empty,
            docRev: 0,
            summary: string.Empty);

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
            Data: templateData,
            EnabledPlugins: enabledPlugins,
            ClosedAtMs: null,
            PinnedAtMs: null,
            SortOrder: req.SortOrder ?? 0.0,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            ExecutionTargetId: trackTargetId);

        var leadProvider = SessionLaunchValidator.NormalizeTaskProviderId(req.Provider) ?? _options.DefaultProvider;
        var leadPermissionMode = RuntimePermissionModes.Resolve(leadProvider, req.PermissionMode);
        var leadSession = new AgentSessionDto(
            Id: leadSessionId,
            TrackId: trackId,
            IsLead: true,
            Title: "Lead",
            Provider: leadProvider,
            Model: req.Model ?? _options.DefaultModel,
            NativeThreadId: null,
            Epoch: 1,
            Status: "idle",
            ArchivedAtMs: null,
            CreatedAtMs: now,
            UpdatedAtMs: now,
            ReasoningEffort: string.IsNullOrWhiteSpace(req.ReasoningEffort) ? null : req.ReasoningEffort.Trim(),
            PermissionMode: leadPermissionMode);

        TrackFlowUpdate? initialFlow = null;
        if (policy is not null)
        {
            var seed = TemplateFlow.ReadSeedNodes(_policyVm, policy, templateInputs);
            if (seed.Length > 0)
            {
                var spec = new FlowSpecDto(1, seed, FlowConstants.SchemaPointer);
                TemplateFlow.RequireValid(TrackFlowValidator.Validate(spec, new(0, []), [], new HashSet<string>(),
                    new Dictionary<string, AgentSessionDto> { [leadSessionId] = leadSession }, leadSessionId,
                    policy, _policyVm, templateInputs, _scheduleCronEvaluator, _timeProvider));
                track = track with { FlowRev = spec.Rev, FlowSpecJson = TrackFlowSerializer.SerializeCanonical(spec) };
                initialFlow = TemplateFlow.BuildSeedUpdate(track, 0, seed, policy, _policyVm, _scheduleCronEvaluator, _timeProvider);
            }
        }
        EventEnvelopeDto[] events =
        [
            new(null, area.Id, trackId, KernelEventKinds.TrackUpdated, JsonHelpers.ToElement(TemplatePresentation.Project(track, policy, _policyVm)), now),
            new(null, area.Id, trackId, KernelEventKinds.SessionUpdated, JsonHelpers.ToElement(leadSession), now)
        ];

        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var id = await _store.CreateTrackAsync(
            new(track, initialReport, policy is null ? null : TemplateHost.Freeze(policy), leadSession,
                initialFlow?.Tasks ?? [], null, preparation, events, dailyDate, systemArea)
            { FlowClocks = initialFlow?.Clocks ?? [], AttachmentIds = attachmentIds },
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
        var env = await ResolveTrackExecutionEnvironmentAsync(trackId, ct).ConfigureAwait(false);
        var mounted = await Actors.GetOrActivate(trackId).EnsureMountedAsync(ct).ConfigureAwait(false);
        return await env.Files
            .BrowseOrReadWorkspaceFileAsync(mounted.MergedCwd, relativePath, maxBytes, ct)
            .ConfigureAwait(false);
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

        var env = await ResolveTrackExecutionEnvironmentAsync(trackId, ct).ConfigureAwait(false);
        var mounted = await Actors.GetOrActivate(trackId).EnsureMountedAsync(ct).ConfigureAwait(false);
        return await env.Files
            .OpenWorkspaceRawFileAsync(mounted.MergedCwd, relativePath, ct)
            .ConfigureAwait(false);
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
        var env = await ResolveTrackExecutionEnvironmentAsync(trackId, ct).ConfigureAwait(false);
        var mounted = await Actors.GetOrActivate(trackId).EnsureMountedAsync(ct).ConfigureAwait(false);
        string? normalizedRelPath = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var sanitizedRel = ResolveContainedRelativePathOrThrow(mounted.MergedCwd, path, env.IsLocal);
            normalizedRelPath = string.IsNullOrEmpty(sanitizedRel) ? null : sanitizedRel;
        }

        return await env.Vcs
            .DiffDetailedAsync(mounted.MergedCwd, fromRev, toRev, normalizedRelPath, ct, searchAncestors: !mounted.ManagedEmptyWorkspace)
            .ConfigureAwait(false);
    }

    private async Task<IExecutionEnvironment> ResolveTrackExecutionEnvironmentAsync(string trackId, CancellationToken ct)
    {
        var track = await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Track '{trackId}' not found.");
        return _executionEnvironments.GetRequired(track.ExecutionTargetId);
    }

    private static string CombineExecutionPath(IExecutionEnvironment env, string basePath, string segment)
    {
        if (env.IsLocal)
        {
            return Path.Combine(basePath, segment);
        }

        var trimmedBase = basePath.TrimEnd('/');
        var trimmedSeg = segment.TrimStart('/');
        return string.IsNullOrEmpty(trimmedBase) ? "/" + trimmedSeg : trimmedBase + "/" + trimmedSeg;
    }

    private static string NormalizeExecutionPath(IExecutionEnvironment env, string path)
    {
        if (env.IsLocal)
        {
            return Path.GetFullPath(path);
        }

        return NormalizePosixPath(path);
    }

    private static string ResolveContainedRelativePathOrThrow(string workspaceRoot, string rawPath, bool isLocal)
    {
        if (isLocal)
        {
            var (_, _, sanitizedRel) = LocalExecutionFileSystem.ResolveContainedWorkspacePathOrThrow(workspaceRoot, rawPath);
            return sanitizedRel;
        }

        var rootFull = NormalizePosixPath(workspaceRoot);
        var rootWithSep = rootFull == "/" ? "/" : rootFull + "/";
        var trimmed = rawPath.Trim();
        string candidateFull;
        if (trimmed.StartsWith('/'))
        {
            var rootedFull = NormalizePosixPath(trimmed);
            candidateFull = string.Equals(rootedFull, rootFull, StringComparison.Ordinal) ||
                            rootedFull.StartsWith(rootWithSep, StringComparison.Ordinal)
                ? rootedFull
                : NormalizePosixPath(rootWithSep + trimmed.TrimStart('/'));
        }
        else
        {
            candidateFull = NormalizePosixPath(rootWithSep + trimmed.Replace('\\', '/'));
        }

        if (!string.Equals(candidateFull, rootFull, StringComparison.Ordinal) &&
            !candidateFull.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw KernelException.Forbidden("Path escapes workspace root.");
        }

        return string.Equals(candidateFull, rootFull, StringComparison.Ordinal)
            ? string.Empty
            : candidateFull[rootWithSep.Length..];
    }

    private static string NormalizePosixPath(string path)
    {
        var replaced = path.Replace('\\', '/').Trim();
        var isAbsolute = replaced.StartsWith('/');
        var parts = replaced.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..")
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (!isAbsolute)
                {
                    stack.Add("..");
                }
            }
            else
            {
                stack.Add(part);
            }
        }

        var joined = string.Join('/', stack);
        if (isAbsolute)
        {
            return "/" + joined;
        }

        return string.IsNullOrEmpty(joined) ? "." : joined;
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

    public Task<ReportReadResult> CommitReportAsync(
        string trackId,
        string callerSessionId,
        ReportCommitRequest req,
        CancellationToken ct = default, McpExecutionOrigin? origin = null, string? expectedPolicyGeneration = null) =>
        Actors.GetOrActivate(trackId).CommitReportBySessionAsync(callerSessionId, req, ct, origin, expectedPolicyGeneration);

    // ------------------- Tasks, Governance & Completion -------------------

    public Task<IReadOnlyList<TaskDto>> ListTasksAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).ListTasksAsync(ct);

    public Task<TaskDto> CancelTaskAsync(
        string trackId,
        string? callerSessionId,
        string taskKeyOrId,
        string? reason = null,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CancelTaskAsync(callerSessionId, taskKeyOrId, reason, ct);

    public Task<TaskDto> CompleteTaskDoneAsync(
        string trackId,
        string callerSessionId,
        JsonElement? workerResult = null,
        CancellationToken ct = default,
        WorkerCompletionOrigin? origin = null,
        FlowOutcome outcome = FlowOutcome.Ok,
        string? summary = null) =>
        Actors.GetOrActivate(trackId).CompleteTaskDoneAsync(callerSessionId, workerResult, ct, origin, outcome, summary);

    public Task<TaskDto> CompleteTaskFailAsync(
        string trackId,
        string callerSessionId,
        string reason,
        JsonElement? workerResult = null,
        CancellationToken ct = default, WorkerCompletionOrigin? origin = null) =>
        Actors.GetOrActivate(trackId).CompleteTaskFailAsync(callerSessionId, reason, workerResult, ct, origin);

    public async Task<string?> GetTrackLeadSessionIdAsync(string trackId, CancellationToken ct) =>
        (await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false))?.LeadSessionId;

    // ------------------- Asks -------------------

    public Task<JsonElement> AskUserAsync(
        string trackId,
        string? callerSessionId,
        IReadOnlyList<AskQuestionDto> questions,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).AskUserAsync(callerSessionId, questions, ct);

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

    public async Task<ITerminalBridgeClient> ResolveTerminalClientAsync(string terminalId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(terminalId))
        {
            var trackId = await _store.FindTrackIdByTerminalAsync(terminalId, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(trackId) &&
                await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false) is { } track)
            {
                return _executionEnvironments.GetRequired(track.ExecutionTargetId).Terminals;
            }
        }

        return _executionEnvironments.Default.Terminals;
    }

    // ------------------- MCP Policy Guard, Tool Dispatch & Schedules -------------------

    public McpSessionPrincipal ObserveMcpExecution(McpSessionPrincipal caller)
    {
        var turnId = Actors.TryGetActive(caller.TrackId, out var actor) ? actor?.ReadActiveTurnId(caller.SessionId) : null;
        return caller with { ExecutionOrigin = new McpExecutionOrigin(turnId) };
    }

    public Task<string> GuardMcpToolCallAsync(
        string trackId,
        string sessionId,
        string mcpServer,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default,
        McpExecutionOrigin? origin = null) =>
        Actors.GetOrActivate(trackId).GuardMcpToolCallAsync(sessionId, mcpServer, toolName, arguments, ct, origin);

    public Task<JsonElement> InvokeKernelMcpToolAsync(
        McpSessionPrincipal caller,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default) =>
        KernelMcpDispatcher.ExecuteGuardedAsync(this, caller, toolName, arguments, ct);

    public Task<bool> CancelPendingWakeAsync(string trackId, string wakeId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).CancelPendingWakeAsync(wakeId, ct);


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
        await _executionEnvironments.DisposeAsync().ConfigureAwait(false);
        DispatchAdmission.Changed -= PublishUpgradeStatus;
        DispatchAdmission.Resumed -= ResumeSchedulers;
        _eventHub.Dispose();
        foreach (var probeLock in _providerProbeLocks.Values)
        {
            probeLock.Dispose();
        }
    }
}
