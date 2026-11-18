using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public sealed class AcpAgentSession : IAgentSession
{
    private sealed record PendingAcpPermission(
        JsonElement RawId,
        string AllowOptionId,
        string RejectOptionId);

    private sealed class TurnStreamState
    {
        public Channel<UnifiedAgentEvent> Events { get; } = Channel.CreateUnbounded<UnifiedAgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public HashSet<string> StartedToolIds { get; } = new(StringComparer.Ordinal);
        public TokenUsage? LatestUsage;
        public volatile bool CancelRequested;
        private int _finished;

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

    private static readonly string[] KnownEffortSuffixes = ["low", "medium", "high", "xhigh", "max", "ultra"];

    internal sealed record AcpRawModel(string ModelId, string? Name, string? Description);

    private readonly JsonRpcStdioConnection _rpc;
    private readonly Action<IAgentSession>? _onDispose;
    private readonly TimeSpan _handshakeTimeout;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingAcpPermission> _pendingPermissions = new(StringComparer.Ordinal);
    private TurnStreamState? _currentTurn;
    private string? _nativeThreadId;
    private string? _lastEmittedThreadId;
    private int _disposed;

    private AcpAgentSession(
        StdioSubprocess process,
        string? initialSessionId,
        TimeSpan handshakeTimeout,
        Action<IAgentSession>? onDispose)
    {
        _nativeThreadId = initialSessionId;
        _handshakeTimeout = handshakeTimeout > TimeSpan.Zero ? handshakeTimeout : TimeSpan.FromSeconds(15);
        _onDispose = onDispose;
        _rpc = new JsonRpcStdioConnection(
            process,
            OnNotification,
            OnServerRequest,
            OnDisconnected);
    }

    public string? NativeThreadId => Volatile.Read(ref _nativeThreadId);

    public bool SupportsNativeSteer => false;

    internal IReadOnlyList<string> AvailableModels { get; private set; } = [];
    internal IReadOnlyList<ProviderModelInfo> AvailableModelCatalog { get; private set; } = [];

    public async Task<bool> TrySetModelAsync(string? model, string? reasoningEffort = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // ACP requires a model ID. Resetting to the provider default needs a fresh session.
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var resolvedModelId = ResolveAcpModelId(model.Trim(), reasoningEffort);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_handshakeTimeout);

        try
        {
            await _rpc.SendRequestAsync("session/set_model", new
            {
                sessionId = NativeThreadId,
                modelId = resolvedModelId
            }, timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == -32601)
        {
            try
            {
                await _rpc.SendRequestAsync("session/set_config_option", new
                {
                    sessionId = NativeThreadId,
                    configId = "model",
                    value = resolvedModelId
                }, timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (JsonRpcRemoteException configEx) when (configEx.Code == -32601)
            {
                return false;
            }
        }
    }

    private string ResolveAcpModelId(string model, string? reasoningEffort)
    {
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            var candidate = $"{model}-{reasoningEffort.Trim()}";
            if (AvailableModels.Contains(candidate, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        if (AvailableModels.Contains(model, StringComparer.Ordinal))
        {
            return model;
        }

        var mediumCandidate = $"{model}-medium";
        if (AvailableModels.Contains(mediumCandidate, StringComparer.Ordinal))
        {
            return mediumCandidate;
        }

        return model;
    }

    internal static async Task<AcpAgentSession> OpenAsync(
        CommandInvocation wrappedCommand,
        HarnessLaunchSpec spec,
        TimeSpan handshakeTimeout,
        Action<IAgentSession>? onDispose,
        CancellationToken ct)
    {
        var process = StdioSubprocess.Start(wrappedCommand);
        var session = new AcpAgentSession(process, spec.ResumeNativeThreadId, handshakeTimeout, onDispose);
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(handshakeTimeout);
            await session.InitializeAndBindSessionAsync(spec, handshakeCts.Token).ConfigureAwait(false);
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

        turnState.CancelRequested = true;
        var cancelParams = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId
        };

        await _rpc.SendNotificationAsync("session/cancel", cancelParams, ct).ConfigureAwait(false);
    }

    public async Task RespondApprovalAsync(
        string requestId,
        bool approved,
        string? reason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (!_pendingPermissions.TryRemove(requestId, out var pending))
        {
            throw new InvalidOperationException($"No pending ACP permission request with id '{requestId}'.");
        }

        var selectedOptionId = approved ? pending.AllowOptionId : pending.RejectOptionId;
        var resultPayload = new Dictionary<string, object?>
        {
            ["outcome"] = new Dictionary<string, object?>
            {
                ["outcome"] = "selected",
                ["optionId"] = selectedOptionId
            },
            ["approved"] = approved,
            ["reason"] = reason
        };

        await _rpc.SendResultResponseAsync(pending.RawId, resultPayload, ct).ConfigureAwait(false);
    }

    public Task<bool> TrySteerAsync(string message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Task.FromResult(false);
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

    private async Task InitializeAndBindSessionAsync(HarnessLaunchSpec spec, CancellationToken ct)
    {
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

        await _rpc.SendRequestAsync("initialize", initParams, ct).ConfigureAwait(false);

        var sessionParams = new Dictionary<string, object?>
        {
            ["cwd"] = Path.GetFullPath(spec.Cwd),
            ["mcpServers"] = McpConfigSerializer.BuildAcpMcpServersList(spec.McpServers)
        };

        if (!string.IsNullOrWhiteSpace(spec.SystemPrompt))
        {
            sessionParams["systemPrompt"] = spec.SystemPrompt;
        }

        string method;
        if (!string.IsNullOrWhiteSpace(spec.ResumeNativeThreadId))
        {
            method = "session/load";
            sessionParams["sessionId"] = spec.ResumeNativeThreadId;
        }
        else
        {
            method = "session/new";
        }

        var response = await _rpc.SendRequestAsync(method, sessionParams, ct).ConfigureAwait(false);
        var resolvedSessionId = TryGetString(response, "sessionId")
                                ?? TryGetString(response, "id")
                                ?? spec.ResumeNativeThreadId;
        if (string.IsNullOrWhiteSpace(resolvedSessionId))
        {
            throw new InvalidOperationException($"ACP '{method}' response did not contain a sessionId.");
        }

        Volatile.Write(ref _nativeThreadId, resolvedSessionId);
        var (rawModels, currentModelId) = ExtractAvailableModels(response);
        AvailableModels = rawModels.Select(m => m.ModelId).ToArray();
        AvailableModelCatalog = BuildAcpModelCatalog(rawModels, currentModelId);
        if (!string.IsNullOrWhiteSpace(spec.Model))
        {
            await TrySetModelAsync(spec.Model, spec.ReasoningEffort, ct).ConfigureAwait(false);
        }
    }

    private static (IReadOnlyList<AcpRawModel> Models, string? CurrentModelId) ExtractAvailableModels(JsonElement response)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AcpRawModel>();
        string? currentModelId = null;
        void Add(string? id, string? name, string? description)
        {
            if (!string.IsNullOrWhiteSpace(id) && ids.Add(id))
            {
                result.Add(new AcpRawModel(id, name, description));
            }
        }

        if (response.ValueKind != JsonValueKind.Object)
        {
            return (result, currentModelId);
        }

        if (response.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
        {
            currentModelId ??= TryGetString(models, "currentModelId");
            if (models.TryGetProperty("availableModels", out var available) && available.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in available.EnumerateArray())
                {
                    Add(TryGetString(model, "modelId"), TryGetString(model, "name"), TryGetString(model, "description"));
                }
            }
        }

        if (response.TryGetProperty("configOptions", out var config) && config.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in config.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object ||
                    !(TryGetString(option, "category") == "model" || TryGetString(option, "id") == "model") ||
                    !option.TryGetProperty("options", out var values) || values.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                currentModelId ??= TryGetString(option, "currentValue");
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("options", out var group) &&
                        group.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in group.EnumerateArray())
                        {
                            Add(TryGetString(item, "value"), TryGetString(item, "name"), TryGetString(item, "description"));
                        }
                    }
                    else
                    {
                        Add(TryGetString(value, "value"), TryGetString(value, "name"), TryGetString(value, "description"));
                    }
                }
            }
        }

        return (result, currentModelId);
    }

    internal static IReadOnlyList<ProviderModelInfo> BuildAcpModelCatalog(
        IReadOnlyList<AcpRawModel> rawModels,
        string? currentModelId)
    {
        if (rawModels.Count == 0)
        {
            return [];
        }

        static (string BaseId, string? Effort) SplitEffortSuffix(string modelId)
        {
            foreach (var suffix in KnownEffortSuffixes)
            {
                var dashSuffix = "-" + suffix;
                if (modelId.EndsWith(dashSuffix, StringComparison.OrdinalIgnoreCase) &&
                    modelId.Length > dashSuffix.Length)
                {
                    return (modelId[..^dashSuffix.Length], suffix);
                }
            }

            return (modelId, null);
        }

        static string StripEffortFromDisplayName(string? name, string? effort)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var trimmed = name.Trim();
            if (effort is null)
            {
                return trimmed;
            }

            var parenSuffix = $" ({effort})";
            if (trimmed.EndsWith(parenSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^parenSuffix.Length].TrimEnd();
            }

            var dashSuffix = $"-{effort}";
            if (trimmed.EndsWith(dashSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^dashSuffix.Length].TrimEnd();
            }

            return trimmed;
        }

        var (currentBase, _) = string.IsNullOrWhiteSpace(currentModelId)
            ? (string.Empty, null)
            : SplitEffortSuffix(currentModelId);

        var order = new List<string>();
        var groups = new Dictionary<string, (string? DisplayName, string? Description, List<string> Efforts, bool HasBare)>(StringComparer.Ordinal);

        foreach (var raw in rawModels)
        {
            var (baseId, effort) = SplitEffortSuffix(raw.ModelId);
            if (!groups.TryGetValue(baseId, out var entry))
            {
                entry = (null, null, [], false);
                order.Add(baseId);
            }

            var cleanName = StripEffortFromDisplayName(raw.Name, effort);
            if (string.IsNullOrWhiteSpace(entry.DisplayName) && !string.IsNullOrWhiteSpace(cleanName))
            {
                entry.DisplayName = cleanName;
            }

            if (string.IsNullOrWhiteSpace(entry.Description) && !string.IsNullOrWhiteSpace(raw.Description))
            {
                entry.Description = raw.Description;
            }

            if (effort is not null)
            {
                if (!entry.Efforts.Contains(effort, StringComparer.OrdinalIgnoreCase))
                {
                    entry.Efforts.Add(effort.ToLowerInvariant());
                }
            }
            else
            {
                entry.HasBare = true;
            }

            groups[baseId] = entry;
        }

        var catalog = new List<ProviderModelInfo>(order.Count);
        for (var i = 0; i < order.Count; i++)
        {
            var baseId = order[i];
            var entry = groups[baseId];
            var sortedEfforts = KnownEffortSuffixes
                .Where(e => entry.Efforts.Contains(e, StringComparer.OrdinalIgnoreCase))
                .Select(e => new ProviderModelEffort(e, e))
                .ToArray();
            var defaultEffort = sortedEfforts.Any(e => e.ReasoningEffort == "medium")
                ? "medium"
                : sortedEfforts.FirstOrDefault()?.ReasoningEffort;
            var isDefault = !string.IsNullOrWhiteSpace(currentBase)
                ? string.Equals(baseId, currentBase, StringComparison.Ordinal)
                : i == 0;

            catalog.Add(new ProviderModelInfo(
                Model: baseId,
                DisplayName: string.IsNullOrWhiteSpace(entry.DisplayName) ? baseId : entry.DisplayName!,
                Description: entry.Description ?? string.Empty,
                IsDefault: isDefault,
                DefaultReasoningEffort: defaultEffort,
                SupportedReasoningEfforts: sortedEfforts));
        }

        return catalog;
    }

    private async Task RunSessionPromptRpcAsync(
        string sessionId,
        TurnInput input,
        TurnStreamState turnState,
        CancellationToken ct)
    {
        var promptParams = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["correlationId"] = input.CorrelationId,
            ["prompt"] = new object[]
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
            var result = await _rpc.SendRequestAsync("session/prompt", promptParams, ct).ConfigureAwait(false);
            var usage = TryParseAcpUsage(result) ?? turnState.LatestUsage;
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
                turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable, IsConclusive: true));
                return;
            }

            if (string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stopReason, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                turnState.TryFinish(new UnifiedAgentEvent.TurnInterrupted());
                return;
            }

            turnState.TryFinish(new UnifiedAgentEvent.TurnCompleted(usage));
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

    private void OnNotification(string method, JsonElement parameters)
    {
        if (!string.Equals(method, "session/update", StringComparison.Ordinal))
        {
            return;
        }

        var turnState = Volatile.Read(ref _currentTurn);
        if (turnState is null || parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var update = parameters.TryGetProperty("update", out var updateObj) && updateObj.ValueKind == JsonValueKind.Object
            ? updateObj
            : parameters;

        var kind = TryGetString(update, "sessionUpdate")
                   ?? TryGetString(update, "type")
                   ?? TryGetString(update, "kind");

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

            case "usage_update":
            {
                var usage = TryParseAcpUsage(update);
                if (usage is not null)
                {
                    turnState.LatestUsage = usage;
                }

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

        var requestId = JsonRpcStdioConnection.ExtractDisplayRequestId(rawId);
        var (allowOptionId, rejectOptionId) = ExtractPermissionOptionIds(parameters);
        _pendingPermissions[requestId] = new PendingAcpPermission(rawId, allowOptionId, rejectOptionId);

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

        var turnState = Volatile.Read(ref _currentTurn);
        turnState?.Events.Writer.TryWrite(new UnifiedAgentEvent.ApprovalRequested(requestId, toolName, args));
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
            turnState.TryFinish(new UnifiedAgentEvent.TurnError("turn_cancelled", "ACP turn was cancelled.", false));
            return;
        }

        turnState.TryFinish(new UnifiedAgentEvent.TurnError(
            "acp_disconnected",
            ex?.Message ?? "ACP server exited unexpectedly.",
            IsRetryable: false));
    }

    private static (string AllowOptionId, string RejectOptionId) ExtractPermissionOptionIds(JsonElement parameters)
    {
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
                if (string.IsNullOrWhiteSpace(optionId))
                {
                    continue;
                }

                var kind = TryGetString(opt, "kind") ?? optionId;
                if (!foundAllow &&
                    (kind.StartsWith("allow", StringComparison.OrdinalIgnoreCase) ||
                     optionId.Contains("allow", StringComparison.OrdinalIgnoreCase)))
                {
                    allowId = optionId;
                    foundAllow = true;
                }
                else if (!foundReject &&
                         (kind.StartsWith("reject", StringComparison.OrdinalIgnoreCase) ||
                          kind.StartsWith("deny", StringComparison.OrdinalIgnoreCase) ||
                          optionId.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
                          optionId.Contains("deny", StringComparison.OrdinalIgnoreCase)))
                {
                    rejectId = optionId;
                    foundReject = true;
                }
            }
        }

        return (allowId, rejectId);
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

    private static TokenUsage? TryParseAcpUsage(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var source = container.TryGetProperty("usage", out var usageObj) && usageObj.ValueKind == JsonValueKind.Object
            ? usageObj
            : container;

        var input = TryGetLong(source, "inputTokens") ?? TryGetLong(source, "input_tokens");
        var output = TryGetLong(source, "outputTokens") ?? TryGetLong(source, "output_tokens");
        if (input is null && output is null)
        {
            return null;
        }

        var cached = TryGetLong(source, "cachedInputTokens")
                     ?? TryGetLong(source, "cachedReadTokens")
                     ?? TryGetLong(source, "cached_input_tokens")
                     ?? 0L;
        var window = TryGetLong(source, "contextWindowTokens")
                     ?? TryGetLong(source, "context_window_tokens")
                     ?? TryGetLong(container, "size");

        return new TokenUsage(input ?? 0L, output ?? 0L, cached, window);
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
