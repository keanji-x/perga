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

    private sealed class TurnStreamState(string correlationId)
    {
        public string CorrelationId { get; } = correlationId;
        public Channel<UnifiedAgentEvent> Events { get; } = Channel.CreateUnbounded<UnifiedAgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public TaskCompletionSource<string> TurnIdReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? TurnId;
        public TokenUsage? LatestUsage;
        public volatile bool CancelRequested;
        private int _finished;

        public bool TrySetTurnId(string? turnId)
        {
            if (string.IsNullOrWhiteSpace(turnId))
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

    private readonly JsonRpcStdioConnection _rpc;
    private readonly Action<IAgentSession>? _onDispose;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedToolCallIds = new(StringComparer.Ordinal);
    private TurnStreamState? _currentTurn;
    private string? _nativeThreadId;
    private string? _lastEmittedThreadId;
    private int _disposed;

    private CodexAgentSession(
        StdioSubprocess process,
        string? initialThreadId,
        Action<IAgentSession>? onDispose)
    {
        _nativeThreadId = initialThreadId;
        _onDispose = onDispose;
        _rpc = new JsonRpcStdioConnection(
            process,
            OnNotification,
            OnServerRequest,
            OnDisconnected);
    }

    public string? NativeThreadId => Volatile.Read(ref _nativeThreadId);

    public bool SupportsNativeSteer => true;

    internal static async Task<CodexAgentSession> OpenAsync(
        CommandInvocation wrappedCommand,
        HarnessLaunchSpec spec,
        TimeSpan handshakeTimeout,
        Action<IAgentSession>? onDispose,
        CancellationToken ct)
    {
        var process = StdioSubprocess.Start(wrappedCommand);
        var session = new CodexAgentSession(process, spec.ResumeNativeThreadId, onDispose);
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(handshakeTimeout);
            await session.InitializeAndBindThreadAsync(spec, handshakeCts.Token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(
        TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _turnLock.WaitAsync(ct).ConfigureAwait(false);
        var turnState = new TurnStreamState(input.CorrelationId);
        Volatile.Write(ref _currentTurn, turnState);

        try
        {
            var threadId = NativeThreadId ?? throw new InvalidOperationException("Codex session has no bound NativeThreadId.");
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
        await _rpc.DisposeAsync().ConfigureAwait(false);
        _turnLock.Dispose();
    }

    private async Task InitializeAndBindThreadAsync(HarnessLaunchSpec spec, CancellationToken ct)
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

        if (!string.IsNullOrWhiteSpace(spec.Model))
        {
            threadParams["model"] = spec.Model;
        }

        if (!string.IsNullOrWhiteSpace(spec.SystemPrompt))
        {
            threadParams["baseInstructions"] = spec.SystemPrompt;
            threadParams["developerInstructions"] = spec.SystemPrompt;
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
        var resolvedThreadId = ExtractThreadId(response) ?? spec.ResumeNativeThreadId;
        if (string.IsNullOrWhiteSpace(resolvedThreadId))
        {
            throw new InvalidOperationException($"Codex '{method}' response did not contain a thread id.");
        }

        Volatile.Write(ref _nativeThreadId, resolvedThreadId);
    }

    private async Task StartTurnRpcAsync(
        string threadId,
        TurnInput input,
        TurnStreamState turnState,
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

        try
        {
            var result = await _rpc.SendRequestAsync("turn/start", turnParams, ct).ConfigureAwait(false);
            var turnId = ExtractTurnId(result);
            turnState.TrySetTurnId(turnId);
            if (turnId is not null)
                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.TurnAccepted(turnId));

            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("turn", out var turnObj) &&
                turnObj.ValueKind == JsonValueKind.Object)
            {
                var usage = TryParseUsage(turnObj) ?? TryParseUsage(result);
                if (usage is not null)
                {
                    turnState.LatestUsage = usage;
                }

                if (turnObj.TryGetProperty("status", out var statusProp) &&
                    statusProp.ValueKind == JsonValueKind.String)
                {
                    var status = statusProp.GetString();
                    if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        var (code, msg, retryable) = ExtractCodexError(turnObj);
                        turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable, IsConclusive: true));
                    }
                }
            }
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
            turnState.TryFinish(new UnifiedAgentEvent.TurnError("codex_transport_error", ex.Message, false));
        }
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
            return;
        }

        switch (method)
        {
            case "turn/started":
            {
                var turnId = ExtractTurnId(parameters);
                turnState.TrySetTurnId(turnId);
                break;
            }

            case "item/agentMessage/delta":
            case "agent_message_delta":
            case "codex/event/agent_message_delta":
            case "codex/event/agent_message_content_delta":
            {
                var delta = TryGetString(parameters, "delta") ?? TryGetString(parameters, "text");
                if (!string.IsNullOrEmpty(delta))
                {
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

                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallStarted(callId, name, args));
                break;
            }

            case "item/completed":
            {
                if (parameters.TryGetProperty("item", out var item) &&
                    item.ValueKind == JsonValueKind.Object &&
                    TryExtractCodexToolItemCompleted(item, out var completedEvt))
                {
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
                turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable));
                break;
            }

            case "turn/completed":
            {
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

                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                    (hasTurnObj && turnObj.TryGetProperty("error", out var turnErr) && turnErr.ValueKind == JsonValueKind.Object))
                {
                    var (code, msg, retryable) = ExtractCodexError(hasTurnObj ? turnObj : parameters);
                    turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable, IsConclusive: true));
                }
                else if (string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase))
                {
                    turnState.TryFinish(new UnifiedAgentEvent.TurnInterrupted());
                }
                else
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

            turnState?.Events.Writer.TryWrite(new UnifiedAgentEvent.ApprovalRequested(requestId, toolName, args));
            return;
        }

        _ = _rpc.SendErrorResponseAsync(rawId, -32601, $"Unsupported client RPC method '{method}'.", CancellationToken.None);
    }

    private void OnDisconnected(Exception? ex)
    {
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

    private static (string Code, string Message, bool Retryable) ExtractCodexError(JsonElement container)
    {
        if (container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty("error", out var errObj))
        {
            if (errObj.ValueKind == JsonValueKind.Object)
            {
                var code = TryGetString(errObj, "code")
                           ?? TryGetString(errObj, "type")
                           ?? "codex_turn_error";
                var msg = TryGetString(errObj, "message") ?? errObj.GetRawText();
                var retry = TryGetBool(errObj, "isRetryable")
                            ?? TryGetBool(errObj, "retryable")
                            ?? TryGetBool(container, "willRetry")
                            ?? false;
                return (code, msg, retry);
            }

            if (errObj.ValueKind == JsonValueKind.String)
            {
                return ("codex_turn_error", errObj.GetString() ?? "Turn error", false);
            }
        }

        var topCode = TryGetString(container, "code") ?? "codex_turn_error";
        var topMsg = TryGetString(container, "message") ?? "Codex turn failed.";
        var topRetry = TryGetBool(container, "isRetryable") ?? TryGetBool(container, "willRetry") ?? false;
        return (topCode, topMsg, topRetry);
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
