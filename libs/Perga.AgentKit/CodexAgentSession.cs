using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public sealed class CodexAgentSession : IAgentSession
{
    private sealed record PendingApproval(JsonElement RawId, string Method);

    private static readonly string[] FallbackDefaultModels = ["gpt-5.4", "gpt-5.3-codex", "o4-mini"];

    private sealed class TurnStreamState
    {
        private readonly HashSet<string> _ignoredTurnIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _earlyCompletions = new(StringComparer.Ordinal);
        private int _hasEmittedProgress;
        private int _fallbackClaimed;
        private int _finished;

        public TurnStreamState(string threadId, TurnInput input, CancellationToken turnToken)
        {
            ThreadId = threadId;
            Input = input;
            TurnToken = turnToken;
        }

        public string ThreadId { get; }
        public TurnInput Input { get; }
        public string CorrelationId => Input.CorrelationId;
        public CancellationToken TurnToken { get; }

        public Channel<UnifiedAgentEvent> Events { get; } = Channel.CreateUnbounded<UnifiedAgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public TaskCompletionSource<string> TurnIdReady { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? TurnId;
        public TokenUsage? LatestUsage;
        public volatile bool CancelRequested;

        public bool HasEmittedProgress => Volatile.Read(ref _hasEmittedProgress) != 0;

        public void MarkProgressEmitted() => Volatile.Write(ref _hasEmittedProgress, 1);

        public bool TryClaimModelFallback() => Interlocked.CompareExchange(ref _fallbackClaimed, 1, 0) == 0;

        public void IgnoreTurnAndResetForRetry(string? failedTurnId)
        {
            lock (_ignoredTurnIds)
            {
                if (!string.IsNullOrWhiteSpace(failedTurnId))
                {
                    _ignoredTurnIds.Add(failedTurnId);
                }

                var current = Volatile.Read(ref TurnId);
                if (!string.IsNullOrWhiteSpace(current))
                {
                    _ignoredTurnIds.Add(current);
                }

                _earlyCompletions.Clear();
                Volatile.Write(ref TurnId, null);
                TurnIdReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public bool IsTurnIgnored(string? turnId)
        {
            if (string.IsNullOrWhiteSpace(turnId))
            {
                return false;
            }

            lock (_ignoredTurnIds)
            {
                return _ignoredTurnIds.Contains(turnId);
            }
        }

        public void BufferEarlyCompletion(string turnId, JsonElement parameters)
        {
            lock (_ignoredTurnIds)
            {
                if (_earlyCompletions.Count < 4)
                    _earlyCompletions.TryAdd(turnId, parameters);
            }
        }

        public JsonElement? TakeEarlyCompletion(string turnId)
        {
            lock (_ignoredTurnIds)
            {
                var found = _earlyCompletions.Remove(turnId, out var parameters);
                _earlyCompletions.Clear();
                return found ? parameters : null;
            }
        }

        public bool TrySetTurnId(string? turnId)
        {
            if (string.IsNullOrWhiteSpace(turnId) || IsTurnIgnored(turnId))
            {
                return false;
            }

            Interlocked.CompareExchange(ref TurnId, turnId, null);
            TurnIdReady.TrySetResult(turnId);
            return true;
        }

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

    private sealed class CompactOperationState(string threadId)
    {
        public string ThreadId { get; } = threadId;
        public string? TurnId;
        public TokenUsage? LatestUsage;
        public TaskCompletionSource<NativeCompactResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly JsonRpcStdioConnection _rpc;
    private readonly string? _configuredModel;
    private readonly Action<IAgentSession>? _onDispose;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _ignoredCompactTurnIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedToolCallIds = new(StringComparer.Ordinal);
    private readonly string? _reasoningEffort;
    private TurnStreamState? _currentTurn;
    private CompactOperationState? _currentCompact;
    private string? _nativeThreadId;
    private string? _lastEmittedThreadId;
    private string? _fallbackModel;
    private bool _configureWorker;
    private int _compactAbandoned;
    private int _disposed;

    private CodexAgentSession(
        StdioSubprocess process,
        string? initialThreadId,
        string? configuredModel,
        string? reasoningEffort,
        Action<IAgentSession>? onDispose)
    {
        _nativeThreadId = initialThreadId;
        _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim();
        _configuredModel = string.IsNullOrWhiteSpace(configuredModel) ? null : configuredModel.Trim();
        _onDispose = onDispose;
        _rpc = new JsonRpcStdioConnection(
            process,
            OnNotification,
            OnServerRequest,
            OnDisconnected);
    }

    public string? NativeThreadId => Volatile.Read(ref _nativeThreadId);

    public bool SupportsNativeSteer => true;
    public bool SupportsNativeCompact => true;
    public WorkerProviderConfiguration? WorkerConfiguration { get; private set; }
    public bool ShutdownConfirmed => _rpc.ShutdownConfirmed;
    public bool SupportsSessionResume => true;
    public bool RequiresTerminalStop => true;

    internal static async Task<CodexAgentSession> OpenAsync(
        CommandInvocation wrappedCommand,
        HarnessLaunchSpec spec,
        TimeSpan handshakeTimeout,
        Action<IAgentSession>? onDispose,
        CancellationToken ct,
        string? sandboxApiKey = null)
    {
        var process = StdioSubprocess.Start(wrappedCommand);
        var session = new CodexAgentSession(process, spec.ResumeNativeThreadId, spec.Model, spec.ReasoningEffort, onDispose);
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(handshakeTimeout);
            await session.InitializeAndBindThreadAsync(spec, sandboxApiKey, handshakeCts.Token).ConfigureAwait(false);
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
        if (Volatile.Read(ref _compactAbandoned) != 0)
        {
            throw new InvalidOperationException("Codex session has an unconfirmed canceled compaction.");
        }

        var threadId = NativeThreadId ?? throw new InvalidOperationException("Codex session has no bound NativeThreadId.");

        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0)
        {
            try { _turnLock.Release(); } catch (ObjectDisposedException) { }
            throw new ObjectDisposedException(nameof(CodexAgentSession));
        }

        if (Volatile.Read(ref _compactAbandoned) != 0)
        {
            try { _turnLock.Release(); } catch (ObjectDisposedException) { }
            throw new InvalidOperationException("Codex session has an unconfirmed canceled compaction.");
        }

        var compactState = new CompactOperationState(threadId);
        Volatile.Write(ref _currentCompact, compactState);
        try
        {
            await _rpc.SendRequestAsync(
                "thread/compact/start",
                new Dictionary<string, object?> { ["threadId"] = threadId },
                ct).ConfigureAwait(false);

            try
            {
                return await compactState.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var activeTurnId = Volatile.Read(ref compactState.TurnId);
                if (!string.IsNullOrWhiteSpace(activeTurnId))
                {
                    _ignoredCompactTurnIds.TryAdd(activeTurnId, 0);
                    try
                    {
                        using var interruptCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await _rpc.SendRequestAsync(
                            "turn/interrupt",
                            new Dictionary<string, object?> { ["threadId"] = threadId, ["turnId"] = activeTurnId },
                            interruptCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort interrupt; if the remote turn has not settled, mark the session unusable below.
                    }
                }

                // Wait briefly for terminal confirmation; if still running, abandon the session so no later turn/compact overlaps.
                try
                {
                    await compactState.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                }
                catch
                {
                    if (!compactState.Completion.Task.IsCompleted)
                    {
                        Interlocked.Exchange(ref _compactAbandoned, 1);
                    }
                }

                throw;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentCompact, null, compactState);
            try { _turnLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public async IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(
        TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _compactAbandoned) != 0)
        {
            throw new InvalidOperationException("Codex session has an unconfirmed canceled compaction.");
        }

        var threadId = NativeThreadId ?? throw new InvalidOperationException("Codex session has no bound NativeThreadId.");
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        var turnState = new TurnStreamState(threadId, input, ct);
        Volatile.Write(ref _currentTurn, turnState);

        try
        {
            if (!string.Equals(_lastEmittedThreadId, threadId, StringComparison.Ordinal))
            {
                _lastEmittedThreadId = threadId;
                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ThreadBound(threadId));
            }

            _ = StartTurnRpcAsync(threadId, input, turnState, ct);

            await foreach (var evt in turnState.Events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentTurn, null, turnState);
            turnState.CompleteQuietly();
            _turnLock.Release();
        }
    }

    public async Task CancelTurnAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var turnState = Volatile.Read(ref _currentTurn);
        if (turnState is null)
        {
            return;
        }

        turnState.CancelRequested = true;
        var threadId = NativeThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        var turnId = await TryWaitTurnIdAsync(turnState, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        var interruptParams = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["turnId"] = turnId
        };

        try
        {
            await _rpc.SendRequestAsync("turn/interrupt", interruptParams, ct).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException)
        {
            // Turn may have already completed on server side.
        }
    }

    public async Task RespondApprovalAsync(
        string requestId,
        bool approved,
        string? reason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_pendingApprovals.TryRemove(requestId, out var pending))
        {
            throw new InvalidOperationException($"No pending Codex approval request with id '{requestId}'.");
        }

        var decision = approved ? "accept" : "decline";
        var payload = new Dictionary<string, object?>
        {
            ["decision"] = decision,
            ["approved"] = approved,
            ["reason"] = reason
        };

        await _rpc.SendResultResponseAsync(pending.RawId, payload, ct).ConfigureAwait(false);
    }

    public Task<bool> TrySetModelAsync(string? model, string? reasoningEffort = null, CancellationToken ct = default) => Task.FromResult(false);

    public async Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return SteerDelivery.Declined;
        }

        var turnState = Volatile.Read(ref _currentTurn);
        var threadId = NativeThreadId;
        if (turnState?.CorrelationId != turnCorrelationId || string.IsNullOrWhiteSpace(threadId))
        {
            return SteerDelivery.Declined;
        }

        var turnId = await TryWaitTurnIdAsync(turnState, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(turnId) || !ReferenceEquals(turnState, Volatile.Read(ref _currentTurn)))
        {
            return SteerDelivery.Declined;
        }

        var steerParams = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["expectedTurnId"] = turnId,
            ["turnId"] = turnId,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = message
                }
            }
        };

        try
        {
            var result = await _rpc.SendRequestAsync("turn/steer", steerParams, ct).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("accepted", out var acceptedProp) &&
                acceptedProp.ValueKind == JsonValueKind.False)
            {
                return SteerDelivery.Declined;
            }

            return result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("turnId", out var responseTurnId)
                && responseTurnId.ValueKind == JsonValueKind.String
                && responseTurnId.GetString() == turnId
                ? SteerDelivery.Accepted
                : SteerDelivery.Unconfirmed;
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == -32600 &&
            (ex.Message.Equals("no active turn to steer", StringComparison.OrdinalIgnoreCase)
             || ex.Message.StartsWith($"expected active turn id `{turnId}` but found `", StringComparison.Ordinal)))
        {
            return SteerDelivery.Declined;
        }
        catch (JsonRpcRemoteException)
        {
            return SteerDelivery.Unconfirmed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return SteerDelivery.Unconfirmed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _onDispose?.Invoke(this);
        var active = Interlocked.Exchange(ref _currentTurn, null);
        active?.CompleteQuietly();
        var activeCompact = Interlocked.Exchange(ref _currentCompact, null);
        activeCompact?.Completion.TrySetException(new ObjectDisposedException(nameof(CodexAgentSession)));
        await _rpc.DisposeAsync().ConfigureAwait(false);
        _turnLock.Dispose();
    }

    private async Task InitializeAndBindThreadAsync(HarnessLaunchSpec spec, string? sandboxApiKey, CancellationToken ct)
    {
        var initParams = new Dictionary<string, object?>
        {
            ["clientInfo"] = new Dictionary<string, object?>
            {
                ["name"] = "perga-agentkit",
                ["version"] = "1.0.0"
            },
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["experimentalApi"] = true
            }
        };

        await _rpc.SendRequestAsync("initialize", initParams, ct).ConfigureAwait(false);
        await _rpc.SendNotificationAsync("initialized", new Dictionary<string, object?>(), ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(sandboxApiKey))
        {
            try
            {
                await _rpc.SendRequestAsync("account/login/start", new Dictionary<string, object?>
                {
                    ["type"] = "apiKey",
                    ["apiKey"] = sandboxApiKey
                }, ct).ConfigureAwait(false);
            }
            catch (JsonRpcRemoteException)
            {
                throw new InvalidOperationException("Codex rejected the sandbox API key.");
            }
        }

        var mcpMap = McpConfigSerializer.BuildCodexMcpServersMap(spec.McpServers);
        var threadParams = new Dictionary<string, object?>
        {
            ["cwd"] = Path.GetFullPath(spec.Cwd),
            ["mcpServers"] = mcpMap,
            ["config"] = new Dictionary<string, object?>
            {
                ["mcp_servers"] = mcpMap
            }
        };

        if ((!spec.ConfigureWorker || string.IsNullOrWhiteSpace(spec.ResumeNativeThreadId)) && !string.IsNullOrWhiteSpace(spec.Model))
        {
            threadParams["model"] = spec.Model;
        }

        if (!string.IsNullOrWhiteSpace(spec.ReasoningEffort))
        {
            threadParams["effort"] = spec.ReasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(spec.SystemPrompt))
        {
            threadParams["baseInstructions"] = spec.SystemPrompt;
            threadParams["developerInstructions"] = spec.SystemPrompt;
        }

        if (!string.IsNullOrWhiteSpace(spec.PermissionMode))
        {
            var (approvalPolicy, sandbox) = MapCodexPermissionMode(spec.PermissionMode.Trim());
            threadParams["approvalPolicy"] = approvalPolicy;
            if (sandbox is not null)
            {
                threadParams["sandbox"] = sandbox;
            }
        }

        string method;
        if (!string.IsNullOrWhiteSpace(spec.ResumeNativeThreadId))
        {
            method = "thread/resume";
            threadParams["threadId"] = spec.ResumeNativeThreadId;
        }
        else
        {
            method = "thread/start";
        }

        var response = await _rpc.SendRequestAsync(method, threadParams, ct).ConfigureAwait(false);
        var resolvedThreadId = ExtractThreadId(response);
        if (string.IsNullOrWhiteSpace(resolvedThreadId))
        {
            throw new InvalidOperationException($"Codex '{method}' response did not contain a thread id.");
        }

        Volatile.Write(ref _nativeThreadId, resolvedThreadId);
        _configureWorker = spec.ConfigureWorker;
        if (_configureWorker)
            WorkerConfiguration = await ReadWorkerConfigurationAsync(spec.Cwd, ct).ConfigureAwait(false);
    }

    private static (string ApprovalPolicy, string? Sandbox) MapCodexPermissionMode(string mode) =>
        mode switch
        {
            "never" => ("never", "workspace-write"),
            "ask" => ("on-request", "workspace-write"),
            "full" => ("never", "danger-full-access"),
            _ => (mode, null)
        };

    private async Task<WorkerProviderConfiguration?> ReadWorkerConfigurationAsync(string cwd, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(600));
        var models = await QueryAsync("model/list", new Dictionary<string, object?>()).ConfigureAwait(false);
        if (models.ValueKind == JsonValueKind.Undefined)
            return null;
        var config = await QueryAsync("config/read", new Dictionary<string, object?> { ["cwd"] = cwd }).ConfigureAwait(false);
        return ModelListParsers.ParseCodexWorkerConfiguration(models, config);

        async Task<JsonElement> QueryAsync(string method, Dictionary<string, object?> args)
        {
            try
            {
                return await _rpc.SendRequestAsync(method, args, timeout.Token).ConfigureAwait(false);
            }
            catch (JsonRpcRemoteException)
            {
                return default;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return default;
            }
        }
    }

    private Task StartTurnRpcAsync(
        string threadId,
        TurnInput input,
        TurnStreamState turnState,
        CancellationToken ct)
    {
        var cachedFallback = Volatile.Read(ref _fallbackModel);
        var allowFallback = input.Model is null && _configuredModel is null && cachedFallback is null;
        return ExecuteTurnStartAsync(threadId, input, input.Model ?? cachedFallback, turnState, allowFallback, ct);
    }

    private async Task ExecuteTurnStartAsync(
        string threadId,
        TurnInput input,
        string? modelOverride,
        TurnStreamState turnState,
        bool allowFallback,
        CancellationToken ct)
    {
        var turnParams = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["correlationId"] = input.CorrelationId,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = input.Prompt
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(_reasoningEffort))
        {
            turnParams["effort"] = _reasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            turnParams["model"] = modelOverride;
        }
        if (input.ReasoningEffort is not null)
            turnParams["effort"] = input.ReasoningEffort;

        try
        {
            await _rpc.SendRequestAsync("turn/start", turnParams, ct, response =>
            {
                var turnId = ExtractTurnId(response);
                if (turnId is null)
                    throw new InvalidOperationException("Codex did not return the native turn ID.");
                if (!ReferenceEquals(Volatile.Read(ref _currentTurn), turnState) || turnState.IsTurnIgnored(turnId))
                    return;

                var turn = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("turn", out var result)
                    ? result : default;
                var failed = string.Equals(TryGetString(turn, "status"), "failed", StringComparison.OrdinalIgnoreCase);
                var early = turnState.TakeEarlyCompletion(turnId);
                if (_configureWorker && !turnState.HasEmittedProgress &&
                    (FindWorkerConfigurationRejection(input, response) ??
                        (early is { } terminal ? FindWorkerConfigurationRejection(input, terminal) : null)) is { } rejection)
                {
                    turnState.TryFinish(new UnifiedAgentEvent.TurnError(rejection.Code, rejection.Message, rejection.Retryable,
                        IsConclusive: true, BeforePrompt: true));
                    return;
                }

                if (!turnState.TrySetTurnId(turnId)) return;
                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.TurnAccepted(turnId));
                if (early is { } completion)
                    OnNotification("turn/completed", completion);

                if (turnState.IsTurnIgnored(turnId) || turn.ValueKind != JsonValueKind.Object)
                    return;

                turnState.LatestUsage = TryParseUsage(turn) ?? TryParseUsage(response) ?? turnState.LatestUsage;
                if (!failed) return;

                var (code, message, retryable) = ExtractCodexError(turn);
                if (allowFallback && CanTriggerModelFallback(turnState, code, message) && turnState.TryClaimModelFallback())
                {
                    turnState.IgnoreTurnAndResetForRetry(turnId);
                    _ = RetryTurnWithFallbackModelAsync(threadId, input, turnState, code, message, retryable,
                        isConclusive: true, ct);
                    return;
                }

                turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, message, retryable, IsConclusive: true));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            turnState.CompleteQuietly();
        }
        catch (JsonRpcRemoteException rpcEx)
        {
            var (code, msg, retryable) = ExtractCodexRpcExceptionError(rpcEx);
            if (_configureWorker && !turnState.HasEmittedProgress && IsWorkerConfigurationRejection(input, code, msg))
            {
                turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable, IsConclusive: true, BeforePrompt: true));
                return;
            }

            if (allowFallback &&
                CanTriggerModelFallback(turnState, code, msg) &&
                turnState.TryClaimModelFallback())
            {
                turnState.IgnoreTurnAndResetForRetry(failedTurnId: null);
                await RetryTurnWithFallbackModelAsync(threadId, input, turnState, code, msg, retryable, isConclusive: false, ct)
                    .ConfigureAwait(false);
                return;
            }

            turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable));
        }
        catch (Exception ex)
        {
            turnState.TryFinish(new UnifiedAgentEvent.TurnError("codex_transport_error", ex.Message, false));
        }
    }

    private async Task RetryTurnWithFallbackModelAsync(
        string threadId,
        TurnInput input,
        TurnStreamState turnState,
        string originalCode,
        string originalMessage,
        bool originalRetryable,
        bool isConclusive,
        CancellationToken ct)
    {
        var rejectedModel = ModelListParsers.ExtractRejectedModelName(originalMessage);
        JsonElement? modelListResult = null;
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(3));
            modelListResult = await _rpc
                .SendRequestAsync("model/list", new Dictionary<string, object?>(), probeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            turnState.CompleteQuietly();
            return;
        }
        catch
        {
            // Fall back to static default model list if model/list fails or times out.
        }

        var fallbackModel = ModelListParsers.SelectCodexFallbackModel(
            modelListResult,
            rejectedModel,
            FallbackDefaultModels);

        if (string.IsNullOrWhiteSpace(fallbackModel))
        {
            turnState.TryFinish(new UnifiedAgentEvent.TurnError(originalCode, originalMessage, originalRetryable, IsConclusive: isConclusive));
            return;
        }

        Volatile.Write(ref _fallbackModel, fallbackModel);
        await ExecuteTurnStartAsync(
                threadId,
                input,
                fallbackModel,
                turnState,
                allowFallback: false,
                ct)
            .ConfigureAwait(false);
    }

    private bool CanTriggerModelFallback(TurnStreamState turnState, string code, string message) =>
        (!_configureWorker || turnState.Input is { Model: null, ReasoningEffort: null }) &&
        _configuredModel is null &&
        Volatile.Read(ref _fallbackModel) is null &&
        !turnState.CancelRequested &&
        !turnState.HasEmittedProgress &&
        IsUnsupportedOrMissingModelError(code, message);

    private static bool IsWorkerConfigurationRejection(TurnInput input, string code, string message) =>
        (input.Model is not null && IsUnsupportedOrMissingModelError(code, message)) ||
        (input.ReasoningEffort is not null && (
            (code.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase) &&
                (code.Contains("unsupported", StringComparison.OrdinalIgnoreCase) || code.Contains("invalid", StringComparison.OrdinalIgnoreCase))) ||
            (message.Contains("reasoning effort", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("not supported", StringComparison.OrdinalIgnoreCase) || message.Contains("invalid", StringComparison.OrdinalIgnoreCase)))));

    private static (string Code, string Message, bool Retryable)? FindWorkerConfigurationRejection(TurnInput input, JsonElement response)
    {
        var turn = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("turn", out var result) &&
            result.ValueKind == JsonValueKind.Object ? result : response;
        var status = TryGetString(response, "status") ?? TryGetString(turn, "status");
        if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)) return null;

        var error = ExtractCodexError(turn);
        return IsWorkerConfigurationRejection(input, error.Code, error.Message) ? error : null;
    }

    private static bool IsUnsupportedOrMissingModelError(string code, string message)
    {
        if (string.Equals(code, "model_not_found", StringComparison.OrdinalIgnoreCase) ||
            code.Contains("model_not_found", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (message.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("is not supported when using Codex", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return message.Contains("model", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (string.Equals(method, "thread/started", StringComparison.Ordinal))
        {
            var newThreadId = ExtractThreadId(parameters);
            if (!string.IsNullOrWhiteSpace(newThreadId))
            {
                Volatile.Write(ref _nativeThreadId, newThreadId);
                var active = Volatile.Read(ref _currentTurn);
                if (active is not null && !string.Equals(_lastEmittedThreadId, newThreadId, StringComparison.Ordinal))
                {
                    _lastEmittedThreadId = newThreadId;
                    active.Events.Writer.TryWrite(new UnifiedAgentEvent.ThreadBound(newThreadId));
                }
            }

            return;
        }

        var turnState = Volatile.Read(ref _currentTurn);
        if (turnState is null)
        {
            if (Volatile.Read(ref _currentCompact) is { } compactState)
            {
                HandleCompactNotification(method, parameters, compactState);
            }

            return;
        }

        var notifTurnId = ExtractTurnId(parameters);
        if (turnState.IsTurnIgnored(notifTurnId))
        {
            return;
        }

        switch (method)
        {
            case "item/agentMessage/delta":
            case "agent_message_delta":
            case "codex/event/agent_message_delta":
            case "codex/event/agent_message_content_delta":
            {
                var delta = TryGetString(parameters, "delta") ?? TryGetString(parameters, "text");
                if (!string.IsNullOrEmpty(delta))
                {
                    turnState.MarkProgressEmitted();
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.TextDelta(delta));
                }

                break;
            }

            case "item/reasoning/summaryTextDelta":
            case "item/reasoning/textDelta":
            case "item/thinking/delta":
            case "codex/event/agent_reasoning_delta":
            {
                var delta = TryGetString(parameters, "delta") ?? TryGetString(parameters, "text");
                if (!string.IsNullOrEmpty(delta))
                {
                    turnState.MarkProgressEmitted();
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ThinkingDelta(delta));
                }

                break;
            }

            case "item/started":
            {
                if (parameters.TryGetProperty("item", out var item) &&
                    item.ValueKind == JsonValueKind.Object &&
                    TryExtractCodexToolItemStarted(item, out var startedEvt))
                {
                    lock (_startedToolCallIds)
                    {
                        _startedToolCallIds.Add(startedEvt.CallId);
                    }

                    turnState.MarkProgressEmitted();
                    turnState.Events.Writer.TryWrite(startedEvt);
                }

                break;
            }

            case "item/toolCall/started":
            case "tool/started":
            {
                var callId = TryGetString(parameters, "callId") ?? TryGetString(parameters, "id") ?? Guid.NewGuid().ToString("N");
                var name = TryGetString(parameters, "name") ?? TryGetString(parameters, "tool") ?? "tool";
                var args = TryGetElement(parameters, "arguments")
                           ?? TryGetElement(parameters, "args")
                           ?? TryGetElement(parameters, "input")
                           ?? JsonRpcStdioConnection.CreateEmptyJsonObject();
                lock (_startedToolCallIds)
                {
                    _startedToolCallIds.Add(callId);
                }

                turnState.MarkProgressEmitted();
                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallStarted(callId, name, args));
                break;
            }

            case "item/completed":
            {
                if (parameters.TryGetProperty("item", out var item) &&
                    item.ValueKind == JsonValueKind.Object &&
                    TryExtractCodexToolItemCompleted(item, out var completedEvt))
                {
                    turnState.MarkProgressEmitted();
                    turnState.Events.Writer.TryWrite(completedEvt);
                }

                break;
            }

            case "item/toolCall/completed":
            case "tool/completed":
            {
                var callId = TryGetString(parameters, "callId") ?? TryGetString(parameters, "id") ?? string.Empty;
                var output = ExtractOutputText(parameters);
                var isError = TryGetBool(parameters, "isError") == true ||
                              string.Equals(TryGetString(parameters, "status"), "failed", StringComparison.OrdinalIgnoreCase);
                turnState.MarkProgressEmitted();
                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallCompleted(callId, output, isError));
                break;
            }

            case "thread/tokenUsage/updated":
            case "turn/usage":
            {
                var usage = TryParseUsage(parameters);
                if (usage is not null)
                {
                    turnState.LatestUsage = usage;
                }

                break;
            }

            case "error":
            case "turn/error":
            case "turn/failed":
            {
                var (code, msg, retryable) = ExtractCodexError(parameters);
                if (CanTriggerModelFallback(turnState, code, msg) &&
                    turnState.TryClaimModelFallback())
                {
                    turnState.IgnoreTurnAndResetForRetry(notifTurnId);
                    _ = RetryTurnWithFallbackModelAsync(
                        turnState.ThreadId,
                        turnState.Input,
                        turnState,
                        code,
                        msg,
                        retryable,
                        isConclusive: false,
                        turnState.TurnToken);
                    break;
                }

                turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable));
                break;
            }

            case "turn/completed":
            {
                if (notifTurnId is null ||
                    (TryGetString(parameters, "threadId") is { } threadId && threadId != turnState.ThreadId))
                    break;

                var usage = TryParseUsage(parameters) ?? turnState.LatestUsage;
                var status = TryGetString(parameters, "status");
                JsonElement turnObj = default;
                var hasTurnObj = parameters.ValueKind == JsonValueKind.Object &&
                                 parameters.TryGetProperty("turn", out turnObj) &&
                                 turnObj.ValueKind == JsonValueKind.Object;
                if (hasTurnObj)
                {
                    status ??= TryGetString(turnObj, "status");
                    usage = TryParseUsage(turnObj) ?? usage;
                }

                if (status is not ("failed" or "completed" or "interrupted" or "cancelled" or "canceled"))
                    break;

                var boundTurnId = Volatile.Read(ref turnState.TurnId);
                if (boundTurnId is null)
                {
                    turnState.BufferEarlyCompletion(notifTurnId, parameters);
                    break;
                }
                if (boundTurnId != notifTurnId)
                    break;

                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    var (code, msg, retryable) = ExtractCodexError(hasTurnObj ? turnObj : parameters);
                    if (CanTriggerModelFallback(turnState, code, msg) &&
                        turnState.TryClaimModelFallback())
                    {
                        turnState.IgnoreTurnAndResetForRetry(notifTurnId);
                        _ = RetryTurnWithFallbackModelAsync(
                            turnState.ThreadId,
                            turnState.Input,
                            turnState,
                            code,
                            msg,
                            retryable,
                            isConclusive: true,
                            turnState.TurnToken);
                        break;
                    }

                    turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable, IsConclusive: true));
                }
                else if (string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
                {
                    turnState.TryFinish(new UnifiedAgentEvent.TurnInterrupted());
                }
                else if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    turnState.TryFinish(new UnifiedAgentEvent.TurnCompleted(usage));
                }

                break;
            }
        }
    }

    private void OnServerRequest(JsonElement rawId, string method, JsonElement parameters)
    {
        var turnState = Volatile.Read(ref _currentTurn);
        var requestId = JsonRpcStdioConnection.ExtractDisplayRequestId(rawId);

        if (method.Contains("Approval", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            _pendingApprovals[requestId] = new PendingApproval(rawId, method);
            var toolName = TryGetString(parameters, "toolName")
                           ?? TryGetString(parameters, "tool")
                           ?? TryGetString(parameters, "command")
                           ?? InferApprovalToolName(method, parameters);
            var args = TryGetElement(parameters, "arguments")
                       ?? TryGetElement(parameters, "args")
                       ?? parameters.Clone();

            turnState?.MarkProgressEmitted();
            turnState?.Events.Writer.TryWrite(new UnifiedAgentEvent.ApprovalRequested(requestId, toolName, args));
            return;
        }

        _ = _rpc.SendErrorResponseAsync(rawId, -32601, $"Unsupported client RPC method '{method}'.", CancellationToken.None);
    }

    private void HandleCompactNotification(string method, JsonElement parameters, CompactOperationState compactState)
    {
        if (ExtractThreadId(parameters) is { } tid && tid != compactState.ThreadId)
        {
            return;
        }

        var notifTurnId = ExtractTurnId(parameters);
        if (notifTurnId is not null && _ignoredCompactTurnIds.ContainsKey(notifTurnId))
        {
            return;
        }

        if (string.Equals(method, "turn/started", StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(notifTurnId))
            {
                Interlocked.CompareExchange(ref compactState.TurnId, notifTurnId, null);
            }

            return;
        }

        var boundTurnId = Volatile.Read(ref compactState.TurnId);
        if (boundTurnId is not null && notifTurnId is not null && notifTurnId != boundTurnId)
        {
            return;
        }

        switch (method)
        {
            case "thread/tokenUsage/updated":
            case "turn/usage":
            {
                var usage = TryParseUsage(parameters);
                if (usage is not null)
                {
                    Volatile.Write(ref compactState.LatestUsage, usage);
                }

                break;
            }

            case "thread/compacted":
            {
                var usage = TryParseUsage(parameters) ?? Volatile.Read(ref compactState.LatestUsage);
                compactState.Completion.TrySetResult(new NativeCompactResult(usage, "Context compacted."));
                break;
            }

            case "turn/completed":
            {
                var usage = TryParseUsage(parameters) ?? Volatile.Read(ref compactState.LatestUsage);
                var status = TryGetString(parameters, "status");
                JsonElement turnObj = default;
                var hasTurnObj = parameters.ValueKind == JsonValueKind.Object &&
                                 parameters.TryGetProperty("turn", out turnObj) &&
                                 turnObj.ValueKind == JsonValueKind.Object;
                if (hasTurnObj)
                {
                    status ??= TryGetString(turnObj, "status");
                    usage = TryParseUsage(turnObj) ?? usage;
                }

                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    var (_, msg, _) = ExtractCodexError(hasTurnObj ? turnObj : parameters);
                    compactState.Completion.TrySetException(new InvalidOperationException(msg));
                }
                else if (string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
                {
                    compactState.Completion.TrySetCanceled();
                }
                else if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    compactState.Completion.TrySetResult(new NativeCompactResult(usage, "Context compacted."));
                }

                break;
            }

            case "error":
            case "turn/error":
            case "turn/failed":
            {
                var (_, msg, _) = ExtractCodexError(parameters);
                compactState.Completion.TrySetException(new InvalidOperationException(msg));
                break;
            }
        }
    }

    private void OnDisconnected(Exception? ex)
    {
        if (Volatile.Read(ref _currentCompact) is { } compactState)
        {
            compactState.Completion.TrySetException(
                ex ?? new IOException("Codex app-server exited unexpectedly during compaction."));
        }

        var turnState = Volatile.Read(ref _currentTurn);
        if (turnState is null)
        {
            return;
        }

        if (turnState.CancelRequested)
        {
            turnState.TryFinish(new UnifiedAgentEvent.TurnError("turn_interrupted", "Turn was interrupted.", false));
            return;
        }

        turnState.TryFinish(new UnifiedAgentEvent.TurnError(
            "codex_disconnected",
            ex?.Message ?? "Codex app-server exited unexpectedly.",
            IsRetryable: false));
    }

    private static async Task<string?> TryWaitTurnIdAsync(
        TurnStreamState turnState,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var existing = Volatile.Read(ref turnState.TurnId);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(timeout);
        try
        {
            return await turnState.TurnIdReady.Task.WaitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Volatile.Read(ref turnState.TurnId);
        }
    }

    private static bool TryExtractCodexToolItemStarted(
        JsonElement item,
        out UnifiedAgentEvent.ToolCallStarted started)
    {
        started = default!;
        var type = TryGetString(item, "type");
        if (string.IsNullOrWhiteSpace(type) ||
            string.Equals(type, "userMessage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "agentMessage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var callId = TryGetString(item, "id") ?? TryGetString(item, "callId") ?? Guid.NewGuid().ToString("N");
        var name = TryGetString(item, "tool")
                   ?? TryGetString(item, "name")
                   ?? TryGetString(item, "command")
                   ?? type;
        var args = TryGetElement(item, "arguments")
                   ?? TryGetElement(item, "args")
                   ?? TryGetElement(item, "input")
                   ?? item.Clone();

        started = new UnifiedAgentEvent.ToolCallStarted(callId, name, args);
        return true;
    }

    private static bool TryExtractCodexToolItemCompleted(
        JsonElement item,
        out UnifiedAgentEvent.ToolCallCompleted completed)
    {
        completed = default!;
        var type = TryGetString(item, "type");
        if (string.IsNullOrWhiteSpace(type) ||
            string.Equals(type, "userMessage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "agentMessage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var callId = TryGetString(item, "id") ?? TryGetString(item, "callId") ?? string.Empty;
        var output = ExtractOutputText(item);
        var status = TryGetString(item, "status");
        var exitCode = TryGetInt(item, "exitCode");
        var hasErrorObj = item.TryGetProperty("error", out var errProp) &&
                          errProp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

        var isError = TryGetBool(item, "isError") == true ||
                      string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(status, "declined", StringComparison.OrdinalIgnoreCase) ||
                      (exitCode is not null && exitCode.Value != 0) ||
                      hasErrorObj;

        completed = new UnifiedAgentEvent.ToolCallCompleted(callId, output, isError);
        return true;
    }

    private static string InferApprovalToolName(string method, JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("command", out var cmdProp))
        {
            if (cmdProp.ValueKind == JsonValueKind.String)
            {
                return "commandExecution";
            }

            if (cmdProp.ValueKind == JsonValueKind.Array)
            {
                return "commandExecution";
            }
        }

        if (method.Contains("fileChange", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("Patch", StringComparison.OrdinalIgnoreCase))
        {
            return "fileChange";
        }

        if (method.Contains("command", StringComparison.OrdinalIgnoreCase))
        {
            return "commandExecution";
        }

        return method;
    }

    private static string? ExtractThreadId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty("threadId", out var tid) && tid.ValueKind == JsonValueKind.String)
        {
            return tid.GetString();
        }

        if (element.TryGetProperty("thread", out var threadObj) &&
            threadObj.ValueKind == JsonValueKind.Object &&
            threadObj.TryGetProperty("id", out var nestedId) &&
            nestedId.ValueKind == JsonValueKind.String)
        {
            return nestedId.GetString();
        }

        return null;
    }

    private static string? ExtractTurnId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty("turnId", out var tid) && tid.ValueKind == JsonValueKind.String)
        {
            return tid.GetString();
        }

        if (element.TryGetProperty("turn", out var turnObj) &&
            turnObj.ValueKind == JsonValueKind.Object &&
            turnObj.TryGetProperty("id", out var nestedId) &&
            nestedId.ValueKind == JsonValueKind.String)
        {
            return nestedId.GetString();
        }

        return null;
    }

    private static TokenUsage? TryParseUsage(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (container.TryGetProperty("tokenUsage", out var tokenUsage) &&
            tokenUsage.ValueKind == JsonValueKind.Object)
        {
            var contextWindow = TryGetLong(tokenUsage, "modelContextWindow")
                                ?? TryGetLong(tokenUsage, "contextWindowTokens");
            if (tokenUsage.TryGetProperty("last", out var lastUsage) && lastUsage.ValueKind == JsonValueKind.Object)
            {
                return BuildTokenUsageFromObject(lastUsage, contextWindow);
            }

            if (tokenUsage.TryGetProperty("total", out var totalUsage) && totalUsage.ValueKind == JsonValueKind.Object)
            {
                return BuildTokenUsageFromObject(totalUsage, contextWindow);
            }

            return BuildTokenUsageFromObject(tokenUsage, contextWindow);
        }

        if (container.TryGetProperty("usage", out var usageObj) &&
            usageObj.ValueKind == JsonValueKind.Object)
        {
            return BuildTokenUsageFromObject(usageObj, TryGetLong(container, "contextWindowTokens"));
        }

        return null;
    }

    private static TokenUsage? BuildTokenUsageFromObject(JsonElement obj, long? fallbackWindow)
    {
        var input = TryGetLong(obj, "inputTokens") ?? TryGetLong(obj, "input_tokens");
        var output = TryGetLong(obj, "outputTokens") ?? TryGetLong(obj, "output_tokens");
        if (input is null && output is null)
        {
            return null;
        }

        var cached = TryGetLong(obj, "cachedInputTokens")
                     ?? TryGetLong(obj, "cached_input_tokens")
                     ?? TryGetLong(obj, "cache_read_input_tokens")
                     ?? 0L;
        var window = TryGetLong(obj, "contextWindowTokens")
                     ?? TryGetLong(obj, "modelContextWindow")
                     ?? fallbackWindow;
        return new TokenUsage(input ?? 0L, output ?? 0L, cached, window);
    }

    private static (string Code, string Message, bool Retryable) ExtractCodexRpcExceptionError(JsonRpcRemoteException rpcEx)
    {
        var defaultCode = $"rpc_{rpcEx.Code.ToString(CultureInfo.InvariantCulture)}";
        var defaultRetryable = rpcEx.Code is -32001 or 429 or >= 500;

        if (rpcEx.ErrorData is { ValueKind: JsonValueKind.Object } dataObj)
        {
            var dataCode = TryGetString(dataObj, "code") ?? TryGetString(dataObj, "type");
            var dataMsg = TryGetString(dataObj, "message");
            if (!string.IsNullOrWhiteSpace(dataCode) || !string.IsNullOrWhiteSpace(dataMsg))
            {
                var (unwrappedCode, unwrappedMsg, unwrappedRetry) = UnwrapEmbeddedJsonError(
                    dataCode ?? defaultCode,
                    dataMsg ?? rpcEx.Message,
                    TryGetBool(dataObj, "isRetryable") ?? TryGetBool(dataObj, "retryable"));
                return (unwrappedCode, unwrappedMsg, unwrappedRetry ?? defaultRetryable);
            }
        }

        var (code, msg, nestedRetry) = UnwrapEmbeddedJsonError(defaultCode, rpcEx.Message, explicitRetryable: null);
        return (code, msg, nestedRetry ?? defaultRetryable);
    }

    private static (string Code, string Message, bool Retryable) ExtractCodexError(JsonElement container)
    {
        if (container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty("error", out var errObj))
        {
            if (errObj.ValueKind == JsonValueKind.Object)
            {
                var rawCode = TryGetString(errObj, "code") ?? TryGetString(errObj, "type");
                var rawMsg = TryGetString(errObj, "message") ?? errObj.GetRawText();
                var explicitRetry = TryGetBool(errObj, "isRetryable")
                                    ?? TryGetBool(errObj, "retryable")
                                    ?? TryGetBool(container, "willRetry");
                var (code, msg, nestedRetry) = UnwrapEmbeddedJsonError(
                    rawCode ?? "codex_turn_error",
                    rawMsg,
                    explicitRetry);
                return (code, msg, nestedRetry ?? false);
            }

            if (errObj.ValueKind == JsonValueKind.String)
            {
                var rawStr = errObj.GetString() ?? "Turn error";
                var (code, msg, nestedRetry) = UnwrapEmbeddedJsonError("codex_turn_error", rawStr, TryGetBool(container, "willRetry"));
                return (code, msg, nestedRetry ?? false);
            }
        }

        var topCode = TryGetString(container, "code") ?? "codex_turn_error";
        var topMsg = TryGetString(container, "message") ?? "Codex turn failed.";
        var topExplicitRetry = TryGetBool(container, "isRetryable") ?? TryGetBool(container, "willRetry");
        var (finalCode, finalMsg, finalRetry) = UnwrapEmbeddedJsonError(topCode, topMsg, topExplicitRetry);
        return (finalCode, finalMsg, finalRetry ?? false);
    }

    private static (string Code, string Message, bool? Retryable) UnwrapEmbeddedJsonError(
        string fallbackCode,
        string rawMessage,
        bool? explicitRetryable)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return (fallbackCode, rawMessage, explicitRetryable);
        }

        var trimmed = rawMessage.Trim();
        var braceIdx = trimmed.IndexOf('{');
        if (braceIdx < 0)
        {
            return (fallbackCode, rawMessage, explicitRetryable);
        }

        var jsonCandidate = braceIdx == 0 ? trimmed : trimmed[braceIdx..];
        try
        {
            using var doc = JsonDocument.Parse(jsonCandidate);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (fallbackCode, rawMessage, explicitRetryable);
            }

            var status = TryGetInt(root, "status");
            if (root.TryGetProperty("error", out var innerErr) && innerErr.ValueKind == JsonValueKind.Object)
            {
                var innerCode = TryGetString(innerErr, "code")
                                ?? TryGetString(innerErr, "type")
                                ?? TryGetString(root, "code");
                var innerMsg = TryGetString(innerErr, "message") ?? TryGetString(root, "message");
                var innerRetry = explicitRetryable
                                 ?? TryGetBool(innerErr, "isRetryable")
                                 ?? TryGetBool(innerErr, "retryable")
                                 ?? (status is { } s ? s == 429 || s >= 500 : null);
                var chosenCode = !string.IsNullOrWhiteSpace(innerCode) &&
                                 (string.Equals(fallbackCode, "codex_turn_error", StringComparison.Ordinal) ||
                                  fallbackCode.StartsWith("rpc_", StringComparison.Ordinal))
                    ? innerCode!
                    : fallbackCode;
                return (chosenCode, string.IsNullOrWhiteSpace(innerMsg) ? rawMessage : innerMsg!, innerRetry);
            }

            var rootCode = TryGetString(root, "code") ?? TryGetString(root, "type");
            var rootMsg = TryGetString(root, "message");
            if (!string.IsNullOrWhiteSpace(rootCode) || !string.IsNullOrWhiteSpace(rootMsg))
            {
                var chosenCode = !string.IsNullOrWhiteSpace(rootCode) &&
                                 (string.Equals(fallbackCode, "codex_turn_error", StringComparison.Ordinal) ||
                                  fallbackCode.StartsWith("rpc_", StringComparison.Ordinal))
                    ? rootCode!
                    : fallbackCode;
                var rootRetry = explicitRetryable ?? (status is { } s ? s == 429 || s >= 500 : null);
                return (chosenCode, string.IsNullOrWhiteSpace(rootMsg) ? rawMessage : rootMsg!, rootRetry);
            }
        }
        catch (JsonException)
        {
            // Message is plain text with braces; preserve as-is.
        }

        return (fallbackCode, rawMessage, explicitRetryable);
    }

    private static string ExtractOutputText(JsonElement item)
    {
        foreach (var propName in new[] { "aggregatedOutput", "output", "stdout", "text" })
        {
            if (item.TryGetProperty(propName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                {
                    return prop.GetString() ?? string.Empty;
                }

                if (prop.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return prop.GetRawText();
                }
            }
        }

        if (item.TryGetProperty("result", out var resProp))
        {
            if (resProp.ValueKind == JsonValueKind.String)
            {
                return resProp.GetString() ?? string.Empty;
            }

            if (resProp.ValueKind == JsonValueKind.Object &&
                resProp.TryGetProperty("content", out var contentArr) &&
                contentArr.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var elem in contentArr.EnumerateArray())
                {
                    var text = TryGetString(elem, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(text);
                    }
                }

                if (parts.Count > 0)
                {
                    return string.Join("\n", parts);
                }
            }

            return resProp.GetRawText();
        }

        return string.Empty;
    }

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

    private static int? TryGetInt(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out var prop) &&
        prop.TryGetInt32(out var val)
            ? val
            : null;

    private static long? TryGetLong(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out var prop) &&
        prop.TryGetInt64(out var val)
            ? val
            : null;

    private static JsonElement? TryGetElement(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? prop.Clone()
            : null;
}
