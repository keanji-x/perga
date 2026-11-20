using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public sealed partial class AcpAgentSession : IAgentSession
{
    private sealed record PendingAcpPermission(
        JsonElement RawId,
        string AllowOptionId,
        string RejectOptionId,
        IReadOnlySet<string> ValidOptionIds);

    private sealed record CompletedContextPrompt(long Id, long? Revision);

    private sealed class TurnStreamState
    {
        public Channel<UnifiedAgentEvent> Events { get; } = Channel.CreateUnbounded<UnifiedAgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public ConcurrentDictionary<string, byte> PendingPermissionIds { get; } = new(StringComparer.Ordinal);
        public object CancelSync { get; } = new();
        public HashSet<string> StartedToolIds { get; } = new(StringComparer.Ordinal);
        public TokenUsage? LatestUsage;
        public long ContextUsageRevision;
        public volatile bool CancelRequested;
        private int _finished;
        public bool IsFinished => Volatile.Read(ref _finished) != 0;

        public bool TryFinish(UnifiedAgentEvent terminalEvent)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
            {
                return false;
            }

            Events.Writer.TryWrite(terminalEvent);
            Events.Writer.TryComplete();
            return true;
        }

        public void CompleteQuietly()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
            {
                Events.Writer.TryComplete();
            }
        }
    }

    private static readonly TimeSpan DefaultCommandsGraceTimeout = TimeSpan.FromSeconds(2);

    private readonly JsonRpcStdioConnection _rpc;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Action<IAgentSession>? _onDispose;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _commandsGraceTimeout;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingAcpPermission> _pendingPermissions = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _availableCommandsObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _idleUsageLock = new();
    private Action<IdleContextUsage>? _idleUsageHandler;
    private IdleContextUsage? _pendingIdleUsage;
    private IdleContextUsage? _pendingIdleUsageWithWindow;
    private TurnStreamState? _currentTurn;
    private string? _nativeThreadId;
    private string? _lastEmittedThreadId;
    private string? _pendingSystemPrompt;
    private (string? ModelId, string? Effort) _confirmedConfiguration;
    private long _contextUsageRevision;
    private long _nextContextPromptId;
    private CompletedContextPrompt? _lastCompletedContextPrompt;
    private bool _useRestoredModelOnPrompt;
    private bool _supportsNativeCompact;
    private int _disposed;

    private AcpAgentSession(
        StdioSubprocess process,
        IReadOnlyDictionary<string, string> environment,
        string? initialSessionId,
        TimeSpan handshakeTimeout,
        Action<IAgentSession>? onDispose,
        TimeSpan? commandsGraceTimeout = null)
    {
        _environment = environment;
        _nativeThreadId = initialSessionId;
        _handshakeTimeout = handshakeTimeout > TimeSpan.Zero ? handshakeTimeout : TimeSpan.FromSeconds(15);
        _commandsGraceTimeout = commandsGraceTimeout is { } grace && grace > TimeSpan.Zero
            ? grace
            : DefaultCommandsGraceTimeout;
        _onDispose = onDispose;
        _rpc = new JsonRpcStdioConnection(
            process,
            OnNotification,
            OnServerRequest,
            OnDisconnected);
    }

    public string? NativeThreadId => Volatile.Read(ref _nativeThreadId);

    public bool SupportsNativeSteer => false;
    public bool SupportsNativeCompact => Volatile.Read(ref _supportsNativeCompact);

    public async ValueTask<bool> ResolveNativeCompactSupportAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _supportsNativeCompact))
        {
            return true;
        }

        if (!_availableCommandsObserved.Task.IsCompleted)
        {
            try
            {
                await _availableCommandsObserved.Task.WaitAsync(_commandsGraceTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Provider did not advertise commands within the grace window; fall back to current flag.
            }
        }

        return SupportsNativeCompact;
    }

    public IReadOnlyList<IdleContextUsage> AttachIdleContextUsageHandler(Action<IdleContextUsage>? handler)
    {
        lock (_idleUsageLock)
        {
            _idleUsageHandler = _disposed == 0 ? handler : null;
            var latest = _pendingIdleUsage;
            var window = _pendingIdleUsageWithWindow;
            _pendingIdleUsage = null;
            _pendingIdleUsageWithWindow = null;
            if (_idleUsageHandler is null || latest is null)
                return [];

            return window is not null && !ReferenceEquals(latest, window) ? [window, latest] : [latest];
        }
    }
    private bool _configureWorker;
    private string? _workerLaunchModel;
    private string? _legacyWorkerModel;
    private AcpSessionOptions? _workerOptions;
    private AcpSessionOptions? _workerDefaults;
    public WorkerProviderConfiguration? WorkerConfiguration => Volatile.Read(ref _workerOptions)?.ToProviderConfiguration(_workerDefaults);
    public bool ShutdownConfirmed => _rpc.ShutdownConfirmed;
    public bool RequiresTerminalStop => true;
    public bool SupportsSessionResume { get; private set; }
    public bool SupportsScopedClose { get; private set; }

    public async Task<bool> TryCloseSessionAsync(CancellationToken ct)
    {
        var sessionId = NativeThreadId;
        if (!SupportsScopedClose || string.IsNullOrWhiteSpace(sessionId))
            return false;

        try
        {
            var response = await _rpc.SendRequestAsync("session/close", new { sessionId }, ct).ConfigureAwait(false);
            return response.ValueKind == JsonValueKind.Object && response.EnumerateObject().All(property => property.NameEquals("_meta"));
        }
        catch (JsonRpcRemoteException error) when (error.Code == -32002)
        {
            // ACP defines unknown session as an idempotent close.
            return true;
        }
    }

    public IReadOnlyList<PermissionModeOption> AvailablePermissionModes => Volatile.Read(ref _permissionModes).Options;

    internal IReadOnlyList<string> AvailableModels { get; private set; } = [];
    internal IReadOnlyList<ProviderModelInfo> AvailableModelCatalog => _modelCatalog.Models;

    public bool CanReuseContextWindow => true;
    public long? ContextUsageRevision
    {
        get
        {
            if (!_configureWorker && _configurationUncertain) return null;
            var revision = Interlocked.Read(ref _contextUsageRevision);
            return revision > 0 ? revision : null;
        }
    }

    private void ConfirmContextBinding(string? modelId, string? effort = null)
    {
        lock (_idleUsageLock)
        {
            var configuration = (modelId, effort);
            if (_contextUsageRevision != 0 && _confirmedConfiguration == configuration)
            {
                return;
            }

            _confirmedConfiguration = configuration;
            _pendingIdleUsage = null;
            _pendingIdleUsageWithWindow = null;
            Interlocked.Increment(ref _contextUsageRevision);
        }
    }

    private void ApplyWorkerOptions(AcpSessionOptions? options)
    {
        lock (_idleUsageLock)
        {
            Volatile.Write(ref _workerOptions, options);
            ConfirmContextBinding(options?.Model?.Current ?? _legacyWorkerModel, options?.Reasoning?.Current);
        }
    }

    internal static async Task<AcpAgentSession> OpenAsync(
        CommandInvocation wrappedCommand,
        HarnessLaunchSpec spec,
        TimeSpan handshakeTimeout,
        Action<IAgentSession>? onDispose,
        CancellationToken ct,
        IReadOnlyList<PermissionModeOption>? extraPermissionModes = null,
        IReadOnlyList<string>? launchArgumentModes = null,
        TimeSpan? commandsGraceTimeout = null)
    {
        var process = StdioSubprocess.Start(wrappedCommand);
        var session = new AcpAgentSession(process, wrappedCommand.Environment, spec.ResumeNativeThreadId, handshakeTimeout, onDispose, commandsGraceTimeout);
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(handshakeTimeout);
            await session.InitializeAndBindSessionAsync(
                spec,
                extraPermissionModes,
                launchArgumentModes,
                handshakeCts.Token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<NativeCompactResult> CompactAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await ResolveNativeCompactSupportAsync(ct).ConfigureAwait(false))
        {
            throw new NotSupportedException("This ACP session does not advertise /compact.");
        }

        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        var turnState = new TurnStreamState();
        Volatile.Write(ref _currentTurn, turnState);

        try
        {
            var sessionId = NativeThreadId ?? throw new InvalidOperationException("ACP session has no bound NativeThreadId.");
            var promptParams = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["correlationId"] = $"compact_{Guid.NewGuid():N}",
                ["prompt"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = "/compact"
                    }
                }
            };

            _ = RunRawPromptRpcAsync(promptParams, turnState, ct);

            // Usage belongs to this operation; a session cache could refer to a previous model or turn.
            return await NativeCompactHelper.DrainAsync(
                turnState.Events.Reader.ReadAllAsync(ct),
                "ACP compaction was interrupted.",
                ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentTurn, null, turnState);
            turnState.CompleteQuietly();
            foreach (var pendingId in turnState.PendingPermissionIds.Keys)
            {
                _pendingPermissions.TryRemove(pendingId, out _);
            }
            _turnLock.Release();
        }
    }

    public async IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(
        TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_configureWorker && ConfigurationState is null)
            throw new SessionConfigurationException(new SessionConfigurationOutcome.Rejected(
                ConfigurationFailureCode.UnconfirmedLaunch, "The complete configuration is unconfirmed.",
                new(_confirmedConfiguration.ModelId, _appliedThoughtLevel, false)));

        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        var turnState = new TurnStreamState();
        Volatile.Write(ref _currentTurn, turnState);

        try
        {
            var sessionId = NativeThreadId ?? throw new InvalidOperationException("ACP session has no bound NativeThreadId.");
            if (!string.Equals(_lastEmittedThreadId, sessionId, StringComparison.Ordinal))
            {
                _lastEmittedThreadId = sessionId;
                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ThreadBound(sessionId));
            }

            _ = RunSessionPromptRpcAsync(sessionId, input, turnState, ct);

            await foreach (var evt in turnState.Events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentTurn, null, turnState);
            turnState.CompleteQuietly();
            foreach (var pendingId in turnState.PendingPermissionIds.Keys)
            {
                _pendingPermissions.TryRemove(pendingId, out _);
            }
            _turnLock.Release();
        }
    }

    public async Task CancelTurnAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var turnState = Volatile.Read(ref _currentTurn);
        var sessionId = NativeThreadId;
        if (turnState is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (turnState.CancelSync)
            turnState.CancelRequested = true;
        await CancelPendingPermissionsAsync(ct).ConfigureAwait(false);
        var cancelParams = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId
        };

        await _rpc.SendNotificationAsync("session/cancel", cancelParams, ct).ConfigureAwait(false);
    }

    private async Task CancelPendingPermissionsAsync(CancellationToken ct)
    {
        foreach (var requestId in _pendingPermissions.Keys.ToArray())
        {
            if (!_pendingPermissions.TryRemove(requestId, out var pending))
            {
                continue;
            }

            await TrySendCancelledPermissionAsync(pending.RawId, ct).ConfigureAwait(false);
        }
    }

    private async Task TrySendCancelledPermissionAsync(JsonElement rawId, CancellationToken ct)
    {
        var cancelledPayload = new Dictionary<string, object?>
        {
            ["outcome"] = new Dictionary<string, object?>
            {
                ["outcome"] = "cancelled"
            }
        };

        try
        {
            await _rpc.SendResultResponseAsync(rawId, cancelledPayload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Subprocess pipe already closed during cancellation.
        }
    }

    public Task RespondApprovalAsync(
        string requestId,
        bool approved,
        string? reason = null,
        CancellationToken ct = default) =>
        RespondApprovalAsync(requestId, approved, selectedOptionId: null, reason, ct);

    public async Task RespondApprovalAsync(
        string requestId,
        bool approved,
        string? selectedOptionId,
        string? reason = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var send = TryClaimApprovalResponse(requestId, approved, selectedOptionId, reason, ct)
            ?? throw new InvalidOperationException($"No pending ACP permission request with id '{requestId}'.");
        await send().ConfigureAwait(false);
    }

    public Func<Task>? TryClaimApprovalResponse(
        string requestId, bool approved, string? selectedOptionId, string? reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (Volatile.Read(ref _disposed) != 0 || !_pendingPermissions.TryRemove(requestId, out var pending))
            return null;

        var optionId = !string.IsNullOrWhiteSpace(selectedOptionId) && pending.ValidOptionIds.Contains(selectedOptionId)
            ? selectedOptionId
            : approved ? pending.AllowOptionId : pending.RejectOptionId;
        var resultPayload = new Dictionary<string, object?>
        {
            ["outcome"] = new Dictionary<string, object?>
            {
                ["outcome"] = "selected",
                ["optionId"] = optionId
            },
            ["approved"] = approved,
            ["reason"] = reason
        };

        return () => _rpc.SendResultResponseAsync(pending.RawId, resultPayload, ct);
    }

    public Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct) =>
        Task.FromResult(SteerDelivery.Declined);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _availableCommandsObserved.TrySetResult();
        lock (_idleUsageLock)
        {
            _idleUsageHandler = null;
            _pendingIdleUsage = null;
            _pendingIdleUsageWithWindow = null;
        }

        _onDispose?.Invoke(this);
        var active = Interlocked.Exchange(ref _currentTurn, null);
        active?.CompleteQuietly();
        await _rpc.DisposeAsync().ConfigureAwait(false);
        _turnLock.Dispose();
    }

    private static bool HasSessionCapability(JsonElement capabilities, string name) =>
        capabilities.ValueKind == JsonValueKind.Object &&
        capabilities.TryGetProperty(name, out var capability) && capability.ValueKind == JsonValueKind.Object;

    private async Task InitializeAndBindSessionAsync(
        HarnessLaunchSpec spec,
        IReadOnlyList<PermissionModeOption>? extraPermissionModes,
        IReadOnlyList<string>? launchArgumentModes,
        CancellationToken ct)
    {
        _extraPermissionModes = Array.AsReadOnly((extraPermissionModes ?? []).ToArray());
        Volatile.Write(ref _permissionModes, new(_extraPermissionModes, null));
        var initParams = new Dictionary<string, object?>
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new Dictionary<string, object?>
            {
                ["fs"] = new Dictionary<string, object?>
                {
                    ["readTextFile"] = false,
                    ["writeTextFile"] = false
                },
                ["terminal"] = false
            },
            ["clientInfo"] = new Dictionary<string, object?>
            {
                ["name"] = "perga-agentkit",
                ["version"] = "1.0.0"
            }
        };

        var initResult = await _rpc.SendRequestAsync("initialize", initParams, ct).ConfigureAwait(false);
        var supportsHttpMcp = AcpStdioHttpMcpBridge.SupportsHttpMcp(initResult);
        var agentCapabilities = initResult.ValueKind == JsonValueKind.Object &&
            initResult.TryGetProperty("agentCapabilities", out var capabilities) ? capabilities : default;
        var sessionCapabilities = agentCapabilities.ValueKind == JsonValueKind.Object &&
            agentCapabilities.TryGetProperty("sessionCapabilities", out var scoped) ? scoped : default;
        var canResume = HasSessionCapability(sessionCapabilities, "resume");
        var canLoad = TryGetBool(agentCapabilities, "loadSession") == true;
        SupportsSessionResume = canResume || canLoad;
        SupportsScopedClose = HasSessionCapability(sessionCapabilities, "close");
        var nodePath = supportsHttpMcp ? null : AcpStdioHttpMcpBridge.ResolveNodeExecutable(_environment);

        var sessionParams = new Dictionary<string, object?>
        {
            ["cwd"] = Path.GetFullPath(spec.Cwd),
            ["mcpServers"] = McpConfigSerializer.BuildAcpMcpServersList(spec.McpServers, supportsHttpMcp, nodePath)
        };

        var isResume = !string.IsNullOrWhiteSpace(spec.ResumeNativeThreadId);
        string method;
        if (isResume)
        {
            if (canResume)
                method = "session/resume";
            else if (canLoad)
                method = "session/load";
            else
                throw new NotSupportedException("This ACP provider did not advertise session resume or load.");

            sessionParams["sessionId"] = spec.ResumeNativeThreadId;
        }
        else
        {
            method = "session/new";
            if (!string.IsNullOrWhiteSpace(spec.SystemPrompt))
            {
                Volatile.Write(ref _pendingSystemPrompt, spec.SystemPrompt);
            }
        }

        _configureWorker = spec.ConfigureWorker;
        _workerLaunchModel = _configureWorker ? spec.Model?.Trim() : null;
        var response = await _rpc.SendRequestAsync(method, sessionParams, ct, res =>
        {
            if (res.ValueKind != JsonValueKind.Object || TryGetBool(res, "success") == false)
            {
                return;
            }

            var id = TryGetString(res, "sessionId")
                     ?? TryGetString(res, "id")
                     ?? spec.ResumeNativeThreadId;
            if (!string.IsNullOrWhiteSpace(id) &&
                (!isResume || string.Equals(id, spec.ResumeNativeThreadId, StringComparison.Ordinal)))
            {
                Volatile.Write(ref _nativeThreadId, id);
                RefreshPermissionModes(res);
            }

            if (_configureWorker)
            {
                var options = AcpSessionOptions.Read(res);
                if (!isResume) _workerDefaults = options;
                ApplyWorkerOptions(options);
                return;
            }

            var (observedModels, observedCurrentModelId) = ExtractAvailableModels(res);
            if (string.IsNullOrWhiteSpace(spec.Model))
            {
                if (!isResume || !string.IsNullOrWhiteSpace(observedCurrentModelId))
                    ConfirmContextBinding(observedCurrentModelId);
                return;
            }

            if (!string.IsNullOrWhiteSpace(observedCurrentModelId))
            {
                var catalog = AcpModelCatalog.Build(observedModels, observedCurrentModelId);
                if (string.Equals(observedCurrentModelId, catalog.Resolve(spec.Model.Trim(), spec.ReasoningEffort), StringComparison.Ordinal))
                    ConfirmContextBinding(observedCurrentModelId);
            }
        }).ConfigureAwait(false);
        if (response.ValueKind != JsonValueKind.Object || TryGetBool(response, "success") == false ||
            (response.TryGetProperty("sessionId", out _) && string.IsNullOrWhiteSpace(TryGetString(response, "sessionId"))) ||
            (response.TryGetProperty("id", out _) && string.IsNullOrWhiteSpace(TryGetString(response, "id"))))
            throw new InvalidOperationException($"ACP '{method}' returned an invalid session result.");
        var resolvedSessionId = TryGetString(response, "sessionId")
                                ?? TryGetString(response, "id")
                                ?? spec.ResumeNativeThreadId;
        if (isResume && !string.Equals(resolvedSessionId, spec.ResumeNativeThreadId, StringComparison.Ordinal))
            throw new InvalidOperationException($"ACP '{method}' did not restore the requested session.");
        if (string.IsNullOrWhiteSpace(resolvedSessionId))
        {
            throw new InvalidOperationException($"ACP '{method}' response did not contain a sessionId.");
        }

        var permissionModes = Volatile.Read(ref _permissionModes);
        _launchArgumentModes = new HashSet<string>(launchArgumentModes ?? [], StringComparer.Ordinal);
        var requestedMode = spec.PermissionMode?.Trim();
        ValidatePermissionMode(requestedMode, permissionModes);
        if (requestedMode is not null && !_launchArgumentModes.Contains(requestedMode))
        {
            var result = await SetPermissionModeAsync(requestedMode, resolvedSessionId, permissionModes, ct).ConfigureAwait(false);
            if (!IsPositiveAcknowledgement(result))
                throw new SessionConfigurationException(new SessionConfigurationOutcome.Rejected(
                    ConfigurationFailureCode.InvalidAcknowledgement, "The provider rejected the permission mode.", new(null, null, false)));
        }

        Volatile.Write(ref _nativeThreadId, resolvedSessionId);
        _useRestoredModelOnPrompt = isResume && string.IsNullOrWhiteSpace(spec.Model);
        if (_configureWorker)
        {
            // Ordinary reuse validates the launch tuple; Worker model/effort changes
            // remain governed by the separate per-turn selection/readback contract.
            _permission = SessionSelection.FromNullable(spec.PermissionMode);
            ConfigurationState = new(SessionConfiguration.FromLaunch(spec), Evidence: ConfigurationEvidence.LaunchAccepted);
            return;
        }

        await InitializeConfigurationAsync(response, spec, isResume, ct).ConfigureAwait(false);
    }

    private async Task RunSessionPromptRpcAsync(
        string sessionId,
        TurnInput input,
        TurnStreamState turnState,
        CancellationToken ct)
    {
        long? workerRevision = null;
        if (_configureWorker)
        {
            try
            {
                var model = Volatile.Read(ref _workerOptions)?.Model;
                if (input.Model is { } requestedModel)
                {
                    if (model is null && requestedModel == _workerLaunchModel)
                        await SetLegacyWorkerLaunchModelAsync(sessionId, requestedModel, ct).ConfigureAwait(false);
                    else
                        await SetWorkerOptionAsync(sessionId, model, requestedModel, model: true, ct).ConfigureAwait(false);
                }
                if (input.ReasoningEffort is not null)
                    await SetWorkerOptionAsync(sessionId, Volatile.Read(ref _workerOptions)?.Reasoning,
                        input.ReasoningEffort, model: false, ct).ConfigureAwait(false);
                lock (_idleUsageLock)
                {
                    var applied = _workerOptions;
                    if ((input.Model is not null && (applied?.Model?.Current ?? _legacyWorkerModel) != input.Model) ||
                        (input.ReasoningEffort is not null && applied?.Reasoning?.Current != input.ReasoningEffort))
                        throw new InvalidOperationException("The ACP session did not confirm the selected model and reasoning effort together.");
                    workerRevision = ContextUsageRevision;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                turnState.CompleteQuietly();
                return;
            }
            catch (Exception ex)
            {
                turnState.TryFinish(new UnifiedAgentEvent.TurnError("worker_configuration_rejected", ex.Message,
                    IsRetryable: false, BeforePrompt: true));
                return;
            }
        }

        var pendingSystem = Interlocked.Exchange(ref _pendingSystemPrompt, null);
        var effectivePrompt = string.IsNullOrWhiteSpace(pendingSystem)
            ? input.Prompt
            : $"<system_instructions>\n{pendingSystem}\n</system_instructions>\n\n{input.Prompt}";

        var promptParams = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["correlationId"] = input.CorrelationId,
            ["prompt"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = effectivePrompt
                }
            }
        };

        await RunRawPromptRpcAsync(promptParams, turnState, ct, workerRevision).ConfigureAwait(false);
    }

    private async Task RunRawPromptRpcAsync(
        Dictionary<string, object?> promptParams,
        TurnStreamState turnState,
        CancellationToken ct,
        long? workerRevision = null)
    {
        try
        {
            if (_useRestoredModelOnPrompt && ContextUsageRevision is null)
                ConfirmContextBinding(null);
            var revision = workerRevision ?? ContextUsageRevision;
            Interlocked.Exchange(ref turnState.ContextUsageRevision, revision ?? 0);
            Volatile.Write(ref _lastCompletedContextPrompt, null);
            var prompt = new CompletedContextPrompt(Interlocked.Increment(ref _nextContextPromptId), revision);
            Task<JsonElement> promptRequest;
            lock (turnState.CancelSync)
            {
                if (turnState.CancelRequested)
                {
                    turnState.TryFinish(new UnifiedAgentEvent.TurnError("turn_interrupted",
                        "ACP stopped before sending the prompt.", false, IsConclusive: true, BeforePrompt: true));
                    return;
                }
                promptRequest = _rpc.SendRequestAsync("session/prompt", promptParams, ct,
                    result => CompleteRawPrompt(result, turnState, prompt));
            }
            await promptRequest.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            turnState.CompleteQuietly();
        }
        catch (JsonRpcRemoteException rpcEx)
        {
            var retryable = rpcEx.Code is -32001 or 429 or >= 500;
            turnState.TryFinish(new UnifiedAgentEvent.TurnError(
                $"rpc_{rpcEx.Code.ToString(CultureInfo.InvariantCulture)}",
                rpcEx.Message,
                retryable));
        }
        catch (Exception ex)
        {
            turnState.TryFinish(new UnifiedAgentEvent.TurnError("acp_transport_error", ex.Message, false));
        }
    }

    private void CompleteRawPrompt(JsonElement result, TurnStreamState turnState, CompletedContextPrompt prompt)
    {
        var stopReason = TryGetString(result, "stopReason");

        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("error", out var errObj) &&
            errObj.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            var code = TryGetString(errObj, "code") ?? "acp_turn_error";
            var msg = errObj.ValueKind == JsonValueKind.String
                ? errObj.GetString() ?? "ACP turn error."
                : TryGetString(errObj, "message") ?? errObj.GetRawText();
            var retryable = TryGetBool(errObj, "isRetryable") ?? TryGetBool(errObj, "retryable") ?? false;
            turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable));
            return;
        }

        if (string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stopReason, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            turnState.TryFinish(new UnifiedAgentEvent.TurnInterrupted());
            return;
        }

        if (stopReason is not ("end_turn" or "max_tokens" or "max_turn_requests" or "refusal"))
        {
            turnState.TryFinish(new UnifiedAgentEvent.TurnError("acp_unconfirmed_turn",
                "The provider returned no recognized stop reason.", IsRetryable: false));
            return;
        }

        var usage = MergeUsage(TryParseAcpUsage(result), turnState.LatestUsage);
        Volatile.Write(ref _lastCompletedContextPrompt, prompt);
        turnState.TryFinish(new UnifiedAgentEvent.TurnCompleted(usage, prompt.Revision, prompt.Id));
    }

    private async Task SetLegacyWorkerLaunchModelAsync(string sessionId, string model, CancellationToken ct)
    {
        if (_legacyWorkerModel == model) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var args = new Dictionary<string, object?> { ["sessionId"] = sessionId, ["modelId"] = model };
        var result = await _rpc.SendRequestAsync("session/set_model", args, timeout.Token, response =>
        {
            if (ConfirmsModel(response, model))
            {
                lock (_idleUsageLock)
                {
                    _legacyWorkerModel = model;
                    ConfirmContextBinding(model);
                }
            }
        }).ConfigureAwait(false);
        if (!ConfirmsModel(result, model))
            throw new InvalidOperationException("The ACP session did not acknowledge the Worker launch model.");
    }

    private async Task SetWorkerOptionAsync(string sessionId, AcpSessionOption? option, string value, bool model, CancellationToken ct)
    {
        if (option is null || !option.Values.Any(choice => choice.Id == value))
            throw new InvalidOperationException("The ACP session did not offer the requested Worker setting.");
        if (option.Current == value) return;

        var args = new Dictionary<string, object?> { ["sessionId"] = sessionId, ["configId"] = option.Id, ["value"] = value };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        AcpSessionOptions? current = null;
        await _rpc.SendRequestAsync("session/set_config_option", args, timeout.Token, response =>
        {
            current = AcpSessionOptions.Read(response);
            if (current is not null)
                ApplyWorkerOptions(current);
        }).ConfigureAwait(false);
        var applied = model ? current?.Model : current?.Reasoning;
        if (applied?.Id != option.Id || applied.Current != value)
            throw new InvalidOperationException("The ACP session did not confirm the requested Worker setting.");
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (!string.Equals(method, "session/update", StringComparison.Ordinal))
        {
            return;
        }

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var notificationSessionId = TryGetString(parameters, "sessionId");
        var currentSessionId = NativeThreadId;
        if (!string.IsNullOrWhiteSpace(notificationSessionId) &&
            !string.IsNullOrWhiteSpace(currentSessionId) &&
            !string.Equals(notificationSessionId, currentSessionId, StringComparison.Ordinal))
        {
            return;
        }

        var update = parameters.TryGetProperty("update", out var updateObj) && updateObj.ValueKind == JsonValueKind.Object
            ? updateObj
            : parameters;

        if (!string.IsNullOrWhiteSpace(currentSessionId) && notificationSessionId == currentSessionId &&
            TryGetString(update, "sessionUpdate") == "config_option_update")
        {
            RefreshPermissionModes(update);
            if (_configureWorker && AcpSessionOptions.Read(update) is { } options)
                ApplyWorkerOptions(options);
        }

        var updateKind = TryGetString(update, "sessionUpdate")
                         ?? TryGetString(update, "type")
                         ?? TryGetString(update, "kind");

        if (string.Equals(updateKind, "available_commands_update", StringComparison.Ordinal))
        {
            if (update.TryGetProperty("availableCommands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
            {
                var hasCommands = false;
                var hasCompact = false;
                foreach (var cmd in cmds.EnumerateArray())
                {
                    hasCommands = true;
                    if (string.Equals(TryGetString(cmd, "name"), "compact", StringComparison.OrdinalIgnoreCase))
                    {
                        hasCompact = true;
                        break;
                    }
                }

                Volatile.Write(ref _supportsNativeCompact, hasCompact);
                if (hasCompact || hasCommands)
                {
                    _availableCommandsObserved.TrySetResult();
                }
            }

            return;
        }

        if (string.Equals(updateKind, "usage_update", StringComparison.Ordinal))
        {
            if (TryParseAcpUsage(update) is { } usage)
            {
                var activeTurn = Volatile.Read(ref _currentTurn);
                if (activeTurn is { IsFinished: false })
                {
                    var revision = Interlocked.Read(ref activeTurn.ContextUsageRevision);
                    if (revision != 0 && revision == ContextUsageRevision)
                    {
                        activeTurn.LatestUsage = MergeUsage(usage, activeTurn.LatestUsage);
                        activeTurn.Events.Writer.TryWrite(new UnifiedAgentEvent.ContextUsageUpdated(usage, revision));
                    }
                }
                else if (!_configureWorker)
                {
                    IdleContextUsage idleUsage;
                    Action<IdleContextUsage>? handler;
                    lock (_idleUsageLock)
                    {
                        if (_disposed != 0 || _contextUsageRevision == 0 || (!_configureWorker && _configurationUncertain))
                            return;

                        var completedPrompt = Volatile.Read(ref _lastCompletedContextPrompt);
                        idleUsage = new IdleContextUsage(usage, _contextUsageRevision, NativeThreadId,
                            completedPrompt is not null && completedPrompt.Revision == _contextUsageRevision ? completedPrompt.Id : null);
                        handler = _idleUsageHandler;
                        if (handler is null)
                        {
                            // Buffer the latest reading and the latest explicit window until the Kernel attaches.
                            _pendingIdleUsage = idleUsage;
                            if (usage.ContextWindowTokens is not null)
                                _pendingIdleUsageWithWindow = idleUsage;
                        }
                    }

                    handler?.Invoke(idleUsage);
                }
            }

            return;
        }

        var turnState = Volatile.Read(ref _currentTurn);
        if (turnState is null) return;

        var kind = updateKind;

        switch (kind)
        {
            case "agent_message_chunk":
            case "text_delta":
            {
                var delta = ExtractChunkText(update);
                if (!string.IsNullOrEmpty(delta))
                {
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.TextDelta(delta));
                }

                break;
            }

            case "agent_thought_chunk":
            case "thought_chunk":
            case "thinking_delta":
            {
                var delta = ExtractChunkText(update);
                if (!string.IsNullOrEmpty(delta))
                {
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ThinkingDelta(delta));
                }

                break;
            }

            case "tool_call":
            case "tool_call_started":
            {
                var callId = TryGetString(update, "toolCallId")
                             ?? TryGetString(update, "callId")
                             ?? TryGetString(update, "id")
                             ?? Guid.NewGuid().ToString("N");
                var name = TryGetString(update, "name")
                           ?? TryGetString(update, "toolName")
                           ?? TryGetString(update, "title")
                           ?? "tool";
                var args = TryGetElement(update, "rawInput")
                           ?? TryGetElement(update, "arguments")
                           ?? TryGetElement(update, "args")
                           ?? TryGetElement(update, "input")
                           ?? JsonRpcStdioConnection.CreateEmptyJsonObject();

                if (turnState.StartedToolIds.Add(callId))
                {
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallStarted(callId, name, args));
                }

                var initialStatus = TryGetString(update, "status");
                if (IsTerminalToolStatus(initialStatus))
                {
                    var output = ExtractToolUpdateOutput(update);
                    var isError = IsErrorToolStatus(initialStatus) || TryGetBool(update, "isError") == true;
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallCompleted(callId, output, isError));
                }

                break;
            }

            case "tool_call_update":
            case "tool_call_completed":
            {
                var status = TryGetString(update, "status");
                if (!string.Equals(kind, "tool_call_completed", StringComparison.Ordinal) &&
                    !IsTerminalToolStatus(status))
                {
                    break;
                }

                var callId = TryGetString(update, "toolCallId")
                             ?? TryGetString(update, "callId")
                             ?? TryGetString(update, "id")
                             ?? string.Empty;
                var output = ExtractToolUpdateOutput(update);
                var isError = IsErrorToolStatus(status) || TryGetBool(update, "isError") == true;
                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallCompleted(callId, output, isError));
                break;
            }


        }
    }

    private void OnServerRequest(JsonElement rawId, string method, JsonElement parameters)
    {
        if (!string.Equals(method, "session/request_permission", StringComparison.Ordinal))
        {
            _ = _rpc.SendErrorResponseAsync(rawId, -32601, $"Unsupported ACP client RPC method '{method}'.", CancellationToken.None);
            return;
        }

        var requestId = $"permission_{Guid.NewGuid():N}";
        var (options, allowOptionId, rejectOptionId) = ExtractPermissionOptions(parameters);
        var validOptionIds = options.Select(o => o.OptionId).ToHashSet(StringComparer.Ordinal);
        _pendingPermissions[requestId] = new PendingAcpPermission(rawId, allowOptionId, rejectOptionId, validOptionIds);

        var turnState = Volatile.Read(ref _currentTurn);
        if (turnState is null || turnState.CancelRequested)
        {
            if (_pendingPermissions.TryRemove(requestId, out var cancelled))
            {
                _ = TrySendCancelledPermissionAsync(cancelled.RawId, CancellationToken.None);
            }

            return;
        }

        var toolName = "permission";
        JsonElement args = JsonRpcStdioConnection.CreateEmptyJsonObject();

        if (parameters.ValueKind == JsonValueKind.Object)
        {
            if (parameters.TryGetProperty("toolCall", out var toolCallObj) &&
                toolCallObj.ValueKind == JsonValueKind.Object)
            {
                toolName = TryGetString(toolCallObj, "name")
                           ?? TryGetString(toolCallObj, "toolName")
                           ?? TryGetString(toolCallObj, "title")
                           ?? toolName;
                args = TryGetElement(toolCallObj, "rawInput")
                       ?? TryGetElement(toolCallObj, "arguments")
                       ?? TryGetElement(toolCallObj, "input")
                       ?? toolCallObj.Clone();
            }
            else
            {
                toolName = TryGetString(parameters, "toolName")
                           ?? TryGetString(parameters, "name")
                           ?? toolName;
                args = TryGetElement(parameters, "arguments")
                       ?? TryGetElement(parameters, "args")
                       ?? parameters.Clone();
            }
        }

        turnState.PendingPermissionIds[requestId] = 0;
        if (!turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ApprovalRequested(
                requestId,
                toolName,
                args,
                options.Count > 0 ? options : null)))
        {
            if (_pendingPermissions.TryRemove(requestId, out var cancelled))
            {
                _ = TrySendCancelledPermissionAsync(cancelled.RawId, CancellationToken.None);
            }
        }
    }

    private void OnDisconnected(Exception? ex)
    {
        _availableCommandsObserved.TrySetResult();
        var turnState = Volatile.Read(ref _currentTurn);
        if (turnState is null)
        {
            return;
        }

        if (turnState.CancelRequested)
        {
            turnState.TryFinish(new UnifiedAgentEvent.TurnError("turn_cancelled", "ACP turn was cancelled.", false));
            return;
        }

        turnState.TryFinish(new UnifiedAgentEvent.TurnError(
            "acp_disconnected",
            ex?.Message ?? "ACP server exited unexpectedly.",
            IsRetryable: false));
    }

    private static (IReadOnlyList<ApprovalOption> Options, string AllowOptionId, string RejectOptionId) ExtractPermissionOptions(JsonElement parameters)
    {
        var options = new List<ApprovalOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allowId = "allow_once";
        var rejectId = "reject_once";
        var foundAllow = false;
        var foundReject = false;

        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("options", out var optionsArr) &&
            optionsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in optionsArr.EnumerateArray())
            {
                if (opt.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var optionId = TryGetString(opt, "optionId") ?? TryGetString(opt, "id");
                if (string.IsNullOrWhiteSpace(optionId) || !seen.Add(optionId))
                {
                    continue;
                }

                var kind = TryGetString(opt, "kind") ?? optionId;
                var label = TryGetString(opt, "name") ?? TryGetString(opt, "label") ?? optionId;
                var isReject = kind.StartsWith("reject", StringComparison.OrdinalIgnoreCase) ||
                               kind.StartsWith("deny", StringComparison.OrdinalIgnoreCase) ||
                               optionId.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
                               optionId.Contains("deny", StringComparison.OrdinalIgnoreCase);
                var isAllow = !isReject &&
                              (kind.StartsWith("allow", StringComparison.OrdinalIgnoreCase) ||
                               optionId.Contains("allow", StringComparison.OrdinalIgnoreCase));

                options.Add(new ApprovalOption(optionId, label, isAllow));

                if (!foundAllow && isAllow)
                {
                    allowId = optionId;
                    foundAllow = true;
                }
                else if (!foundReject && isReject)
                {
                    rejectId = optionId;
                    foundReject = true;
                }
            }
        }

        return (options, allowId, rejectId);
    }

    private static string ExtractChunkText(JsonElement update)
    {
        if (update.TryGetProperty("content", out var contentProp))
        {
            if (contentProp.ValueKind == JsonValueKind.String)
            {
                return contentProp.GetString() ?? string.Empty;
            }

            if (contentProp.ValueKind == JsonValueKind.Object)
            {
                return TryGetString(contentProp, "text") ?? string.Empty;
            }
        }

        return TryGetString(update, "delta") ?? TryGetString(update, "text") ?? string.Empty;
    }

    private static string ExtractToolUpdateOutput(JsonElement update)
    {
        if (update.TryGetProperty("rawOutput", out var rawOut))
        {
            if (rawOut.ValueKind == JsonValueKind.String)
            {
                return rawOut.GetString() ?? string.Empty;
            }

            if (rawOut.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return rawOut.GetRawText();
            }
        }

        if (update.TryGetProperty("output", out var outProp))
        {
            if (outProp.ValueKind == JsonValueKind.String)
            {
                return outProp.GetString() ?? string.Empty;
            }

            if (outProp.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return outProp.GetRawText();
            }
        }

        if (update.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in contentArr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var directText = TryGetString(item, "text");
                if (!string.IsNullOrEmpty(directText))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }

                    sb.Append(directText);
                    continue;
                }

                if (item.TryGetProperty("content", out var innerContent) &&
                    innerContent.ValueKind == JsonValueKind.Object)
                {
                    var nestedText = TryGetString(innerContent, "text");
                    if (!string.IsNullOrEmpty(nestedText))
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }

                        sb.Append(nestedText);
                    }
                }
            }

            return sb.ToString();
        }

        return string.Empty;
    }

    private static bool IsTerminalToolStatus(string? status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);

    private static bool IsErrorToolStatus(string? status) =>
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);

    private static TokenUsage? MergeUsage(TokenUsage? usage, TokenUsage? latest) =>
        usage is null ? latest : usage with
        {
            ContextWindowTokens = usage.ContextWindowTokens ?? latest?.ContextWindowTokens
        };

    private static TokenUsage? TryParseAcpUsage(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var source = container.TryGetProperty("usage", out var usageObj) && usageObj.ValueKind == JsonValueKind.Object
            ? usageObj
            : container;

        var input = TryGetLong(source, "inputTokens")
                    ?? TryGetLong(source, "input_tokens")
                    ?? TryGetLong(source, "used");
        var output = TryGetLong(source, "outputTokens") ?? TryGetLong(source, "output_tokens");
        var used = TryGetLong(source, "used");
        if (input is null && output is null && used is null)
        {
            return null;
        }

        var cached = TryGetLong(source, "cachedInputTokens")
                     ?? TryGetLong(source, "cachedReadTokens")
                     ?? TryGetLong(source, "cached_input_tokens")
                     ?? 0L;
        if (input < 0 || output < 0 || used < 0 || cached < 0)
        {
            return null;
        }

        long? window = null;
        if (TryGetWindow(source, out var rawWindow) || TryGetWindow(container, out rawWindow))
        {
            if (rawWindow.ValueKind != JsonValueKind.Number || !rawWindow.TryGetInt64(out var value) || value <= 0)
                return null;
            window = value;
        }

        return new TokenUsage(input ?? 0L, output ?? 0L, cached, window, used);
    }

    private static bool TryGetWindow(JsonElement usage, out JsonElement window) =>
        usage.TryGetProperty("contextWindowTokens", out window) ||
        usage.TryGetProperty("context_window_tokens", out window) ||
        usage.TryGetProperty("size", out window);

    private static string? TryGetString(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool? TryGetBool(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;

    private static long? TryGetLong(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var val)
            ? val
            : null;

    private static JsonElement? TryGetElement(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? prop.Clone()
            : null;
}
