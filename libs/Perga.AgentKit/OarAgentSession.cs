using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public sealed class OarAgentSession : IAgentSession
{
    private const int CompactUnsupportedRpcCode = -32010;
    private const int CompactAbortedRpcCode = -32011;

    private sealed class TurnStreamState
    {
        public Channel<UnifiedAgentEvent> Events { get; } = Channel.CreateUnbounded<UnifiedAgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private int _finished;

        public bool TryFinish(UnifiedAgentEvent terminalEvent)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return false;
            Events.Writer.TryWrite(terminalEvent);
            Events.Writer.TryComplete();
            return true;
        }

        public void CompleteQuietly()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0) Events.Writer.TryComplete();
        }
    }

    private readonly JsonRpcStdioConnection _rpc;
    private readonly Action<IAgentSession>? _onDispose;
    private readonly bool _configureWorker;
    private readonly SemaphoreSlim _shutdownLock = new(1, 1);
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly object _idleUsageLock = new();
    private Action<IdleContextUsage>? _idleUsageHandler;
    private IdleContextUsage? _pendingIdleUsage;
    private TurnStreamState? _currentTurn;
    private string? _nativeThreadId;
    private string? _lastEmittedThreadId;
    private ConfirmedConfiguration? _configurationState;
    private WorkerProviderConfiguration? _workerConfiguration;
    private bool _supportsNativeSteer;
    private bool _supportsNativeCompact;
    private bool _supportsSessionResume = true;
    private long _contextUsageRevision = 1;
    private long _nextContextPromptId;
    private int _poisoned;
    private int _disposed;

    private OarAgentSession(StdioSubprocess process, string? initialSessionId, bool configureWorker, Action<IAgentSession>? onDispose)
    {
        _nativeThreadId = initialSessionId;
        _configureWorker = configureWorker;
        _onDispose = onDispose;
        _rpc = new JsonRpcStdioConnection(process, OnNotification, OnServerRequest, OnDisconnected);
    }

    public string? NativeThreadId => Volatile.Read(ref _nativeThreadId);
    public bool SupportsNativeSteer => Volatile.Read(ref _supportsNativeSteer);
    public bool SupportsNativeCompact => Volatile.Read(ref _supportsNativeCompact);
    public bool CanReuseContextWindow => true;
    public long? ContextUsageRevision => Interlocked.Read(ref _contextUsageRevision);
    public WorkerProviderConfiguration? WorkerConfiguration => Volatile.Read(ref _workerConfiguration);
    public bool ShutdownConfirmed => _rpc.ShutdownConfirmed;
    public bool SupportsSessionResume => Volatile.Read(ref _supportsSessionResume);
    public bool RequiresTerminalStop => true;
    public bool SupportsScopedClose => true;
    public SessionConfigurationCapabilities ConfigurationCapabilities => SessionConfigurationCapabilities.LaunchOnly;
    public ConfirmedConfiguration? ConfigurationState => Volatile.Read(ref _configurationState);

    internal static async Task<OarAgentSession> OpenAsync(
        CommandInvocation invocation,
        HarnessLaunchSpec spec,
        string oarRuntimeId,
        string? executableOverride,
        TimeSpan handshakeTimeout,
        Action<IAgentSession>? onDispose,
        CancellationToken ct, Action<System.Diagnostics.Process>? killProcess = null)
    {
        var session = new OarAgentSession(StdioSubprocess.Start(invocation, killProcess), spec.ResumeNativeThreadId, spec.ConfigureWorker, onDispose);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(handshakeTimeout > TimeSpan.Zero ? handshakeTimeout : TimeSpan.FromSeconds(30));
            await session.InitializeSessionAsync(spec, oarRuntimeId, executableOverride, cts.Token).ConfigureAwait(false);
            return session;
        }
        catch (Exception error)
        {
            await session.ShutdownAfterFailedOpenAsync(error).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task ShutdownAfterFailedOpenAsync(Exception openError)
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanupError)
        {
            throw new ProviderOpenShutdownUnconfirmedException(this, new AggregateException(openError, cleanupError));
        }
        if (!ShutdownConfirmed)
            throw new ProviderOpenShutdownUnconfirmedException(this, openError);
    }

    public async ValueTask<bool> ResolveNativeCompactSupportAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _supportsNativeCompact)) return true;

        try
        {
            var result = await _rpc.SendRequestAsync("session/compact_support", new { graceTimeoutMs = 2000 }, ct)
                .ConfigureAwait(false);
            var supported = TryGetBool(result, "supported") == true;
            Volatile.Write(ref _supportsNativeCompact, supported);
            return supported;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Volatile.Read(ref _supportsNativeCompact); }
    }

    public IReadOnlyList<IdleContextUsage> AttachIdleContextUsageHandler(Action<IdleContextUsage>? handler)
    {
        lock (_idleUsageLock)
        {
            _idleUsageHandler = _disposed == 0 ? handler : null;
            var pending = _pendingIdleUsage;
            _pendingIdleUsage = null;
            return _idleUsageHandler is not null && pending is not null ? [pending] : [];
        }
    }

    public async Task<bool> TryCloseSessionAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0) return true;
        try
        {
            var result = await _rpc.SendRequestAsync("session/close", new { }, ct).ConfigureAwait(false);
            return TryGetBool(result, "closed") == true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async Task<NativeCompactResult> CompactAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _poisoned) != 0, this);
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        var poisonAndDispose = false;
        try
        {
            var response = await _rpc.SendRequestAsync("session/compact", new { timeoutMs = 120_000 }, ct)
                .ConfigureAwait(false);
            var summary = TryGetString(response, "summary");
            return new NativeCompactResult(
                Usage: TryParseUsage(response),
                Summary: string.IsNullOrWhiteSpace(summary) ? "Context compacted." : summary,
                ContextUsageRevision: ContextUsageRevision,
                CompletedPromptId: Interlocked.Increment(ref _nextContextPromptId));
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == CompactUnsupportedRpcCode)
        {
            throw new NotSupportedException(ex.Message, ex);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == CompactAbortedRpcCode)
        {
            poisonAndDispose = true;
            throw new OperationCanceledException(ex.Message, ex, ct);
        }
        catch (OperationCanceledException)
        {
            // Caller stopped waiting while Node/OAR compaction may still be running; never reuse this handle.
            poisonAndDispose = true;
            throw;
        }
        catch
        {
            poisonAndDispose = true;
            throw;
        }
        finally
        {
            if (poisonAndDispose)
            {
                Interlocked.Exchange(ref _poisoned, 1);
            }
            _turnLock.Release();
            if (poisonAndDispose)
            {
                await DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(
        TurnInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _poisoned) != 0, this);

        var activeConfig = Volatile.Read(ref _configurationState);
        if (!string.IsNullOrWhiteSpace(input.Model) &&
            !string.Equals(input.Model.Trim(), activeConfig?.NativeModel ?? activeConfig?.Selection.Model.ValueOrNull, StringComparison.Ordinal))
        {
            yield return new UnifiedAgentEvent.TurnError(
                "OAR_TURN_MODEL_MISMATCH",
                $"OAR session is bound to model '{activeConfig?.NativeModel ?? activeConfig?.Selection.Model.ValueOrNull ?? "default"}' and cannot switch to '{input.Model}' mid-session.",
                IsRetryable: false,
                IsConclusive: true,
                BeforePrompt: true);
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(input.ReasoningEffort) &&
            !string.Equals(input.ReasoningEffort.Trim(), activeConfig?.NativeEffort ?? activeConfig?.Selection.Effort.ValueOrNull, StringComparison.Ordinal))
        {
            yield return new UnifiedAgentEvent.TurnError(
                "OAR_TURN_EFFORT_MISMATCH",
                $"OAR session is bound to reasoning effort '{activeConfig?.NativeEffort ?? activeConfig?.Selection.Effort.ValueOrNull ?? "default"}' and cannot switch to '{input.ReasoningEffort}' mid-session.",
                IsRetryable: false,
                IsConclusive: true,
                BeforePrompt: true);
            yield break;
        }

        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        var turn = new TurnStreamState();
        Volatile.Write(ref _currentTurn, turn);

        try
        {
            if (NativeThreadId is { Length: > 0 } sessionId &&
                !string.Equals(_lastEmittedThreadId, sessionId, StringComparison.Ordinal))
            {
                _lastEmittedThreadId = sessionId;
                turn.Events.Writer.TryWrite(new UnifiedAgentEvent.ThreadBound(sessionId));
            }

            _ = RunPromptRpcAsync(input, turn, ct);
            await foreach (var evt in turn.Events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return evt;
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentTurn, null, turn);
            turn.CompleteQuietly();
            _turnLock.Release();
        }
    }

    public async Task CancelTurnAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _currentTurn) is null) return;
        try { await _rpc.SendRequestAsync("session/abort", new { }, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
    }

    public Func<Task>? TryClaimApprovalResponse(
        string requestId, bool approved, string? selectedOptionId, string? reason, CancellationToken ct) => null;

    public Task<SessionConfigurationOutcome> ConfigureAsync(
        SessionConfiguration configuration, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _poisoned) != 0)
        {
            return Task.FromResult<SessionConfigurationOutcome>(
                new SessionConfigurationOutcome.RequiresReopen("Reopen OAR session after unconfirmed compaction."));
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var current = Volatile.Read(ref _configurationState);
        if (current is not null)
        {
            if (current.Selection == configuration &&
                (configuration.Model is not SessionSelection.Explicit explicitModel ||
                 string.Equals(current.NativeModel, explicitModel.Value, StringComparison.Ordinal)))
            {
                return Task.FromResult<SessionConfigurationOutcome>(new SessionConfigurationOutcome.Applied(current));
            }

            if (_configureWorker &&
                current.Selection.Permission == configuration.Permission &&
                MatchesWorkerSelection(configuration.Model, current.Selection.Model, current.NativeModel) &&
                MatchesWorkerSelection(configuration.Effort, current.Selection.Effort, current.NativeEffort))
            {
                return Task.FromResult<SessionConfigurationOutcome>(new SessionConfigurationOutcome.Applied(current));
            }
        }
        return Task.FromResult<SessionConfigurationOutcome>(
            new SessionConfigurationOutcome.RequiresReopen("Reopen OAR session to apply updated configuration."));
    }

    private static bool MatchesWorkerSelection(SessionSelection requested, SessionSelection activeSelection, string? nativeValue) =>
        requested switch
        {
            SessionSelection.ProviderDefault => activeSelection is SessionSelection.ProviderDefault,
            SessionSelection.Explicit explicitVal => string.Equals(nativeValue, explicitVal.Value, StringComparison.Ordinal),
            _ => false
        };

    public async Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!SupportsNativeSteer || string.IsNullOrWhiteSpace(message)) return SteerDelivery.Declined;
        try
        {
            var result = await _rpc.SendRequestAsync("session/steer",
                new { message, correlationId = turnCorrelationId }, ct).ConfigureAwait(false);
            return string.Equals(TryGetString(result, "delivery"), "accepted", StringComparison.OrdinalIgnoreCase)
                ? SteerDelivery.Accepted
                : SteerDelivery.Declined;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return SteerDelivery.Unconfirmed; }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdownLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ShutdownConfirmed) return;
            var firstAttempt = Interlocked.Exchange(ref _disposed, 1) == 0;
            if (firstAttempt)
            {
                lock (_idleUsageLock) { _idleUsageHandler = null; _pendingIdleUsage = null; }
                Interlocked.Exchange(ref _currentTurn, null)?.CompleteQuietly();
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _rpc.SendRequestAsync("session/close", new { }, cts.Token).ConfigureAwait(false);
                }
                catch { /* Process shutdown remains authoritative. */ }
            }

            try { await _rpc.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                if (ShutdownConfirmed) _onDispose?.Invoke(this);
            }
        }
        finally { _shutdownLock.Release(); }
    }

    private async Task InitializeSessionAsync(
        HarnessLaunchSpec spec, string oarRuntimeId, string? executableOverride, CancellationToken ct)
    {
        var (permissionEnv, permissionLaunchArgs) = ResolvePermissionPolicy(oarRuntimeId, spec.PermissionMode);

        JsonElement result;
        try
        {
            result = await _rpc.SendRequestAsync("session/open", new Dictionary<string, object?>
            {
                ["runtime"] = oarRuntimeId,
                ["cwd"] = spec.Cwd,
                ["model"] = spec.Model,
                ["effort"] = spec.ReasoningEffort,
                ["resume"] = spec.ResumeNativeThreadId,
                ["systemPrompt"] = spec.SystemPrompt,
                ["executable"] = executableOverride,
                ["mcpServers"] = SerializeMcpServers(spec.McpServers),
                ["env"] = permissionEnv,
                ["launchArgs"] = permissionLaunchArgs,
                ["configureWorker"] = spec.ConfigureWorker
            }, ct).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex) when (spec.ResumeNativeThreadId is not null && IsSessionNotFoundError(ex))
        {
            throw new ProviderSessionNotFoundException(ex.Message, ex);
        }
        catch (JsonRpcRemoteException ex) when (IsConfigurationError(ex))
        {
            throw new SessionConfigurationException(new SessionConfigurationOutcome.Rejected(
                ConfigurationFailureCode.OperationFailed, ex.Message, new ObservedConfiguration(null, null, false)));
        }
        catch (JsonRpcRemoteException ex)
        {
            throw new InvalidOperationException($"OAR failed to open '{oarRuntimeId}' session: {ex.Message}", ex);
        }

        if (TryGetString(result, "sessionId") is { Length: > 0 } sessionId)
            Volatile.Write(ref _nativeThreadId, sessionId);
        Volatile.Write(ref _supportsNativeSteer, TryGetBool(result, "supportsNativeSteer") == true);
        Volatile.Write(ref _supportsNativeCompact, TryGetBool(result, "supportsNativeCompact") == true);
        Volatile.Write(ref _supportsSessionResume, TryGetBool(result, "supportsSessionResume") != false);

        var revision = Interlocked.Read(ref _contextUsageRevision);
        Volatile.Write(ref _configurationState, new ConfirmedConfiguration(
            Selection: SessionConfiguration.FromLaunch(spec),
            NativeModel: TryGetString(result, "model") ?? spec.Model,
            NativeEffort: TryGetString(result, "effort") ?? spec.ReasoningEffort,
            ContextUsageRevision: revision,
            Evidence: ConfigurationEvidence.ProviderAcknowledged));

        if (spec.ConfigureWorker && TryParseWorkerConfiguration(result) is { } workerConfig)
            Volatile.Write(ref _workerConfiguration, workerConfig);

        if (TryParseUsage(result, "contextUsage") is { } initialUsage)
            PublishIdleContextUsage(initialUsage, revision);
    }

    private async Task RunPromptRpcAsync(TurnInput input, TurnStreamState turn, CancellationToken ct)
    {
        try
        {
            var response = await _rpc.SendRequestAsync("session/prompt",
                new { prompt = input.Prompt, correlationId = input.CorrelationId }, ct).ConfigureAwait(false);
            var status = TryGetString(response, "status") ?? "completed";
            var usage = TryParseUsage(response);

            if (status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                turn.TryFinish(new UnifiedAgentEvent.TurnCompleted(
                    usage, Interlocked.Read(ref _contextUsageRevision), Interlocked.Increment(ref _nextContextPromptId)));
            }
            else if (status.Equals("aborted", StringComparison.OrdinalIgnoreCase))
            {
                turn.TryFinish(new UnifiedAgentEvent.TurnInterrupted());
            }
            else
            {
                turn.TryFinish(new UnifiedAgentEvent.TurnError(
                    TryGetString(response, "code") ?? "OAR_TURN_FAILED",
                    TryGetString(response, "message") ?? "OAR turn failed.",
                    TryGetBool(response, "isRetryable") == true,
                    IsConclusive: true,
                    BeforePrompt: TryGetBool(response, "beforePrompt") == true));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            turn.TryFinish(new UnifiedAgentEvent.TurnInterrupted());
        }
        catch (Exception ex)
        {
            turn.TryFinish(new UnifiedAgentEvent.TurnError("OAR_RPC_ERROR", ex.Message, false, IsConclusive: true));
        }
    }

    private void OnNotification(string method, JsonElement p)
    {
        if (!method.Equals("oar/event", StringComparison.Ordinal) || p.ValueKind != JsonValueKind.Object) return;
        var turn = Volatile.Read(ref _currentTurn);
        switch (TryGetString(p, "kind"))
        {
            case "text_delta" when TryGetRawString(p, "delta") is { Length: > 0 } d:
                turn?.Events.Writer.TryWrite(new UnifiedAgentEvent.TextDelta(d));
                break;
            case "thinking_delta" when TryGetRawString(p, "delta") is { Length: > 0 } d:
                turn?.Events.Writer.TryWrite(new UnifiedAgentEvent.ThinkingDelta(d));
                break;
            case "tool_call_started":
                turn?.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallStarted(
                    TryGetString(p, "callId") ?? Guid.NewGuid().ToString("N"),
                    TryGetString(p, "name") ?? "tool",
                    p.TryGetProperty("args", out var a) ? a.Clone() : JsonRpcStdioConnection.CreateEmptyJsonObject()));
                break;
            case "tool_call_completed":
                turn?.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallCompleted(
                    TryGetString(p, "callId") ?? string.Empty,
                    TryGetRawString(p, "output") ?? string.Empty,
                    TryGetBool(p, "isError") == true));
                break;
            case "context_usage" when TryParseUsage(p) is { } usage:
            {
                var rev = Interlocked.Read(ref _contextUsageRevision);
                if (turn is not null) turn.Events.Writer.TryWrite(new UnifiedAgentEvent.ContextUsageUpdated(usage, rev));
                else PublishIdleContextUsage(usage, rev);
                break;
            }
            case "model_changed" when TryGetString(p, "model") is { Length: > 0 } newModel:
            {
                var current = Volatile.Read(ref _configurationState);
                if (current is not null && !string.Equals(current.NativeModel, newModel, StringComparison.Ordinal))
                {
                    var nextRev = Interlocked.Increment(ref _contextUsageRevision);
                    Volatile.Write(ref _configurationState, current with { NativeModel = newModel, ContextUsageRevision = nextRev });
                }
                break;
            }
            case "exited" when turn is not null:
                turn.TryFinish(new UnifiedAgentEvent.TurnError(
                    "OAR_EXITED", TryGetString(p, "reason") ?? "OAR runtime process exited.", false, IsConclusive: true));
                break;
        }
    }

    private void OnServerRequest(JsonElement id, string method, JsonElement parameters)
    {
        _ = _rpc.SendErrorResponseAsync(id, -32601, $"Unsupported bridge callback '{method}'.", CancellationToken.None);
    }

    private void OnDisconnected(Exception? error) =>
        Volatile.Read(ref _currentTurn)?.TryFinish(new UnifiedAgentEvent.TurnError(
            "OAR_DISCONNECTED", error?.Message ?? "OAR bridge disconnected.", false, IsConclusive: true));

    private void PublishIdleContextUsage(TokenUsage usage, long revision)
    {
        var idle = new IdleContextUsage(usage, revision, NativeThreadId);
        Action<IdleContextUsage>? handler;
        lock (_idleUsageLock)
        {
            handler = _idleUsageHandler;
            if (handler is null) { _pendingIdleUsage = idle; return; }
        }
        handler(idle);
    }

    private static List<Dictionary<string, object?>> SerializeMcpServers(IReadOnlyDictionary<string, McpServerConfig>? servers)
    {
        var list = new List<Dictionary<string, object?>>();
        if (servers is null) return list;
        foreach (var (name, config) in servers)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            switch (config)
            {
                case McpServerConfig.HttpMcpServer http:
                    list.Add(new() { ["name"] = name, ["type"] = "http", ["url"] = http.Url, ["headers"] = http.Headers });
                    break;
                case McpServerConfig.StdioMcpServer stdio:
                    list.Add(new() { ["name"] = name, ["type"] = "stdio", ["command"] = stdio.Command, ["args"] = stdio.Args, ["env"] = stdio.Env });
                    break;
            }
        }
        return list;
    }

    private static (Dictionary<string, string>? Env, string[]? LaunchArgs) ResolvePermissionPolicy(
        string oarRuntimeId, string? permissionMode)
    {
        var mode = string.IsNullOrWhiteSpace(permissionMode) ? null : permissionMode.Trim();
        if (string.Equals(oarRuntimeId, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return mode switch
            {
                null or "never" => (new Dictionary<string, string> { ["OAR_CODEX_SANDBOX"] = "workspace-write" }, null),
                "full" or "bypassPermissions" => (new Dictionary<string, string> { ["OAR_CODEX_SANDBOX"] = "danger-full-access" }, null),
                _ => throw new SessionConfigurationException(new SessionConfigurationOutcome.Unsupported(
                    ConfigurationDimension.Permission,
                    $"OAR Codex runtime does not support permission mode '{mode}'."))
            };
        }

        if (string.Equals(oarRuntimeId, "claude", StringComparison.OrdinalIgnoreCase))
        {
            return mode switch
            {
                null or "dontAsk" => (null, ["--permission-mode", "dontAsk"]),
                "acceptEdits" => (null, ["--permission-mode", "acceptEdits"]),
                "plan" => (null, ["--permission-mode", "plan"]),
                "bypassPermissions" or "full" => (null, null),
                _ => throw new SessionConfigurationException(new SessionConfigurationOutcome.Unsupported(
                    ConfigurationDimension.Permission,
                    $"OAR Claude runtime does not support permission mode '{mode}'."))
            };
        }

        if (mode is null or "bypassPermissions" or "full")
        {
            return (null, null);
        }

        throw new SessionConfigurationException(new SessionConfigurationOutcome.Unsupported(
            ConfigurationDimension.Permission,
            $"OAR runtime '{oarRuntimeId}' runs non-interactively and does not support permission mode '{mode}'."));
    }

    private static bool IsSessionNotFoundError(JsonRpcRemoteException ex) =>
        (ex.ErrorData is { ValueKind: JsonValueKind.Object } d &&
         TryGetString(d, "errorName") is "SessionNotFoundError") ||
        ProviderSessionNotFoundException.IsMissingSession(ex.Message);

    private static bool IsConfigurationError(JsonRpcRemoteException ex) =>
        ex.ErrorData is { ValueKind: JsonValueKind.Object } d &&
        TryGetString(d, "errorName") is "UnsupportedOptionError" or "ModelConfirmationError";

    private static WorkerProviderConfiguration? TryParseWorkerConfiguration(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty("workerConfiguration", out var cfg) ||
            cfg.ValueKind != JsonValueKind.Object)
            return null;

        var models = new List<WorkerModelOption>();
        if (cfg.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modelsArr.EnumerateArray())
            {
                var id = TryGetString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = TryGetString(item, "name") ?? id;
                var efforts = new List<string>();
                if (item.TryGetProperty("reasoningEfforts", out var effArr) && effArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var eff in effArr.EnumerateArray())
                    {
                        if (eff.ValueKind == JsonValueKind.String && eff.GetString()?.Trim() is { Length: > 0 } level)
                            efforts.Add(level);
                    }
                }
                models.Add(new WorkerModelOption(id, name, efforts, TryGetString(item, "defaultReasoningEffort")));
            }
        }

        var defaultModel = TryGetString(cfg, "defaultModel");
        var defaultEffort = TryGetString(cfg, "defaultReasoningEffort");
        return models.Count == 0 && defaultModel is null && defaultEffort is null
            ? null
            : new WorkerProviderConfiguration(models, defaultModel, defaultEffort);
    }

    internal static TokenUsage? TryParseUsage(JsonElement container, string propertyName = "usage")
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty(propertyName, out var u) || u.ValueKind != JsonValueKind.Object)
            return null;
        var (input, output, cached, window, used) = (
            TryGetInt64(u, "inputTokens") ?? 0,
            TryGetInt64(u, "outputTokens") ?? 0,
            TryGetInt64(u, "cachedInputTokens") ?? 0,
            TryGetInt64(u, "contextWindowTokens"),
            TryGetInt64(u, "usedTokens"));
        return input == 0 && output == 0 && cached == 0 && window is null && used is null
            ? null
            : new TokenUsage(input, output, cached, window, used);
    }

    private static string? TryGetString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim() : null;

    private static string? TryGetRawString(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool? TryGetBool(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static long? TryGetInt64(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n : null;
}
