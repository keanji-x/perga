using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public sealed class ClaudeAgentSession : IAgentSession
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private sealed record PendingClaudeApproval(string RequestId, JsonElement OriginalInput);

    private sealed class TurnStreamState
    {
        public Channel<UnifiedAgentEvent> Events { get; } = Channel.CreateUnbounded<UnifiedAgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public ConcurrentDictionary<string, byte> PendingApprovalIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StartedToolIds { get; } = new(StringComparer.Ordinal);
        public TokenUsage? LatestAssistantUsage;
        public long? ContextWindowTokens;
        public bool SawStreamedText;
        public bool SawStreamedThinking;
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

    private readonly StdioSubprocess _process;
    private readonly string _mcpConfigFilePath;
    private readonly Action<IAgentSession>? _onDispose;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingClaudeApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pumpTask;
    private TurnStreamState? _currentTurn;
    private string? _observedNativeThreadId;
    private readonly string? _resumeNativeThreadId;
    private string? _terminalReaderFailure;
    private readonly TaskCompletionSource _identityObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _lastEmittedThreadId;
    private bool _supportsNativeCompact = true;
    private int _disposed;
    private readonly string _initializeRequestId = $"init_{Guid.NewGuid():N}";
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ClaudeAgentSession(
        StdioSubprocess process,
        string mcpConfigFilePath,
        string? initialThreadId,
        Action<IAgentSession>? onDispose)
    {
        _process = process;
        _mcpConfigFilePath = mcpConfigFilePath;
        _resumeNativeThreadId = initialThreadId;
        _onDispose = onDispose;
        _pumpTask = Task.Run(PumpNdjsonStdoutAsync);
    }

    public string? NativeThreadId => Volatile.Read(ref _observedNativeThreadId);

    public bool SupportsNativeSteer => false;
    public bool CanReuseContextWindow => true;
    public long? ContextUsageRevision => 0;
    public bool SupportsNativeCompact => Volatile.Read(ref _supportsNativeCompact);
    public bool ShutdownConfirmed => _process.ShutdownConfirmed;
    public bool RequiresTerminalStop => true;
    public bool SupportsSessionResume => true;

    public string McpConfigFilePath => _mcpConfigFilePath;

    internal static async Task<ClaudeAgentSession> OpenAsync(
        ProviderCommandOverride commandSpec,
        OsSandboxWrapper sandboxWrapper,
        HarnessLaunchSpec spec,
        IReadOnlyDictionary<string, string> baseEnvironment,
        Action<IAgentSession>? onDispose,
        CancellationToken ct,
        bool supportsEffort = false,
        TimeSpan? handshakeTimeout = null)
    {
        if (!supportsEffort && !string.IsNullOrWhiteSpace(spec.ReasoningEffort))
            throw new SessionConfigurationException(new SessionConfigurationOutcome.Unsupported(
                ConfigurationDimension.Effort, "The resolved Claude command does not advertise --effort."));
        var tempJsonPath = Path.Combine(
            Path.GetTempPath(),
            $"perga-claude-mcp-{Guid.NewGuid():N}.json");

        try
        {
            var mcpJson = McpConfigSerializer.SerializeClaudeMcpConfigJson(spec.McpServers);
            await File.WriteAllTextAsync(tempJsonPath, mcpJson, Utf8NoBom, ct).ConfigureAwait(false);

            var cliArgs = new List<string>(16);
            if (commandSpec.PrependArgs is not null)
            {
                cliArgs.AddRange(commandSpec.PrependArgs);
            }

            cliArgs.Add("--print");
            cliArgs.Add("--verbose");
            cliArgs.Add("--input-format");
            cliArgs.Add("stream-json");
            cliArgs.Add("--output-format");
            cliArgs.Add("stream-json");
            cliArgs.Add("--mcp-config");
            cliArgs.Add(tempJsonPath);

            if (!string.IsNullOrWhiteSpace(spec.ResumeNativeThreadId))
            {
                cliArgs.Add("--resume");
                cliArgs.Add(spec.ResumeNativeThreadId);
            }

            if (!string.IsNullOrWhiteSpace(spec.Model))
            {
                cliArgs.Add("--model");
                cliArgs.Add(spec.Model);
            }

            if (!string.IsNullOrWhiteSpace(spec.ReasoningEffort))
            {
                cliArgs.Add("--effort");
                cliArgs.Add(spec.ReasoningEffort);
            }

            if (!string.IsNullOrWhiteSpace(spec.SystemPrompt))
            {
                cliArgs.Add("--append-system-prompt");
                cliArgs.Add(spec.SystemPrompt);
            }

            if (!string.IsNullOrWhiteSpace(spec.PermissionMode))
            {
                cliArgs.Add("--permission-mode");
                cliArgs.Add(spec.PermissionMode.Trim());
            }

            var rawInvocation = new CommandInvocation(
                commandSpec.FileName,
                cliArgs,
                spec.Cwd,
                baseEnvironment);

            var wrappedInvocation = sandboxWrapper.Wrap(
                rawInvocation,
                spec.Sandbox,
                spec.OverlayMounts,
                auxiliaryReadOnlyFiles: [tempJsonPath]);

            var subprocess = StdioSubprocess.Start(wrappedInvocation);
            var session = new ClaudeAgentSession(
                subprocess,
                tempJsonPath,
                spec.ResumeNativeThreadId,
                onDispose)
            {
                _supportsEffort = supportsEffort
            };
            try
            {
                await subprocess.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "control_request",
                    request_id = session._initializeRequestId,
                    request = new { subtype = "initialize" }
                }), ct).ConfigureAwait(false);
                var handshake = string.IsNullOrWhiteSpace(spec.ResumeNativeThreadId)
                    ? session._initialized.Task
                    : Task.WhenAll(session._initialized.Task, session._identityObserved.Task);
                await handshake.WaitAsync(handshakeTimeout ?? TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                if (subprocess.HasExited || session._pumpTask.IsCompleted || Volatile.Read(ref session._terminalReaderFailure) is not null)
                    throw new SessionConfigurationException(new SessionConfigurationOutcome.Rejected(
                        ConfigurationFailureCode.OperationFailed, "Claude exited during initialization.", new(null, null, false)));
                if (spec.ResumeNativeThreadId is not null && session.NativeThreadId != spec.ResumeNativeThreadId)
                    throw new SessionConfigurationException(new SessionConfigurationOutcome.Rejected(
                        ConfigurationFailureCode.InvalidAcknowledgement, "Claude did not restore the requested session identity.", new(null, null, false)));
                session.ConfigurationState = new(SessionConfiguration.FromLaunch(spec), ContextUsageRevision: session.ContextUsageRevision,
                    Evidence: ConfigurationEvidence.LaunchAccepted);
                return session;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception error)
            {
                await session.DisposeAsync().ConfigureAwait(false);
                if (spec.ResumeNativeThreadId is not null &&
                    ProviderSessionNotFoundException.IsMissingSession(error.Message))
                    throw new ProviderSessionNotFoundException(error.Message, error);
                if (error is SessionConfigurationException) throw;
                throw new SessionConfigurationException(new SessionConfigurationOutcome.Rejected(
                    ConfigurationFailureCode.OperationFailed, error.Message, new(null, null, false)));
            }
        }
        catch
        {
            TryDeleteTempFile(tempJsonPath);
            throw;
        }
    }

    public async Task<NativeCompactResult> CompactAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!SupportsNativeCompact)
        {
            throw new NotSupportedException("Claude session does not advertise /compact.");
        }

        return await NativeCompactHelper.DrainAsync(
            StreamTurnAsync(new TurnInput("/compact", $"compact_{Guid.NewGuid():N}"), ct),
            "Claude compaction was interrupted.",
            ct).ConfigureAwait(false);
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
            var existingThreadId = NativeThreadId;
            if (!string.IsNullOrWhiteSpace(existingThreadId) &&
                !string.Equals(_lastEmittedThreadId, existingThreadId, StringComparison.Ordinal))
            {
                _lastEmittedThreadId = existingThreadId;
                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ThreadBound(existingThreadId));
            }

            if (Volatile.Read(ref _terminalReaderFailure) is { } readerFailure)
            {
                turnState.TryFinish(new UnifiedAgentEvent.TurnError("claude_disconnected", readerFailure, false));
            }
            else if (_process.HasExited)
            {
                var stderr = _process.GetStderrSummary();
                var detail = string.IsNullOrWhiteSpace(stderr) ? "Claude CLI process has already exited." : stderr;
                turnState.TryFinish(new UnifiedAgentEvent.TurnError("claude_process_exited", detail, false));
            }
            else
            {
                var userFrame = new Dictionary<string, object?>
                {
                    ["type"] = "user",
                    ["session_id"] = existingThreadId ?? "default",
                    ["correlation_id"] = input.CorrelationId,
                    ["parent_tool_use_id"] = null,
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = input.Prompt
                    }
                };

                var jsonLine = JsonSerializer.Serialize(userFrame);
                await _process.WriteLineAsync(jsonLine, ct).ConfigureAwait(false);
            }

            await foreach (var evt in turnState.Events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentTurn, null, turnState);
            turnState.CompleteQuietly();
            foreach (var pendingId in turnState.PendingApprovalIds.Keys)
            {
                _pendingApprovals.TryRemove(pendingId, out _);
            }
            _turnLock.Release();
        }
    }

    public async Task CancelTurnAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var turnState = Volatile.Read(ref _currentTurn);
        if (turnState is null)
        {
            return;
        }

        turnState.CancelRequested = true;

        foreach (var requestId in _pendingApprovals.Keys.ToArray())
        {
            if (!_pendingApprovals.TryRemove(requestId, out var pending))
            {
                continue;
            }

            await TryWriteApprovalResponseAsync(pending, approved: false, reason: "Turn was interrupted.", ct).ConfigureAwait(false);
        }

        _process.SendInterruptSignal();
    }

    public async Task RespondApprovalAsync(
        string requestId,
        bool approved,
        string? reason = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var send = TryClaimApprovalResponse(requestId, approved, selectedOptionId: null, reason, ct)
            ?? throw new InvalidOperationException($"No pending Claude approval request with id '{requestId}'.");
        await send().ConfigureAwait(false);
    }

    public Func<Task>? TryClaimApprovalResponse(
        string requestId, bool approved, string? selectedOptionId, string? reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (Volatile.Read(ref _disposed) != 0 || !_pendingApprovals.TryRemove(requestId, out var pending))
            return null;

        return () => WriteApprovalResponseAsync(pending, approved, reason, ct);
    }

    private async Task TryWriteApprovalResponseAsync(
        PendingClaudeApproval pending,
        bool approved,
        string? reason,
        CancellationToken ct)
    {
        try
        {
            await WriteApprovalResponseAsync(pending, approved, reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Subprocess pipe already closed during cancellation.
        }
    }

    private async Task WriteApprovalResponseAsync(
        PendingClaudeApproval pending,
        bool approved,
        string? reason,
        CancellationToken ct)
    {
        var innerResponse = new Dictionary<string, object?>
        {
            ["behavior"] = approved ? "allow" : "deny",
            ["approved"] = approved
        };

        if (approved)
        {
            innerResponse["updatedInput"] = pending.OriginalInput;
        }
        else
        {
            innerResponse["message"] = string.IsNullOrWhiteSpace(reason)
                ? "User denied tool execution."
                : reason;
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            innerResponse["reason"] = reason;
        }

        var controlResponse = new Dictionary<string, object?>
        {
            ["type"] = "control_response",
            ["response"] = new Dictionary<string, object?>
            {
                ["subtype"] = "success",
                ["request_id"] = pending.RequestId,
                ["response"] = innerResponse
            }
        };

        var jsonLine = JsonSerializer.Serialize(controlResponse);
        await _process.WriteLineAsync(jsonLine, ct).ConfigureAwait(false);
    }

    private bool _supportsEffort;
    public SessionConfigurationCapabilities ConfigurationCapabilities => SessionConfigurationCapabilities.LaunchOnly with
    {
        Effort = SessionConfigurationCapabilities.LaunchDimension(_supportsEffort ? ConfigurationSupport.Supported : ConfigurationSupport.Unsupported)
    };
    private ConfirmedConfiguration? _configurationState;
    public ConfirmedConfiguration? ConfigurationState
    {
        get => Volatile.Read(ref _terminalReaderFailure) is null ? Volatile.Read(ref _configurationState) : null;
        private set => Volatile.Write(ref _configurationState, value);
    }
    public Task<SessionConfigurationOutcome> ConfigureAsync(SessionConfiguration configuration, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_process.HasExited)
        {
            ConfigurationState = null;
            return Task.FromResult<SessionConfigurationOutcome>(new SessionConfigurationOutcome.Rejected(
                ConfigurationFailureCode.OperationFailed, "Claude process has exited.", new(null, null, false)));
        }
        if (!_supportsEffort && configuration.Effort is SessionSelection.Explicit)
            return Task.FromResult<SessionConfigurationOutcome>(new SessionConfigurationOutcome.Unsupported(
                ConfigurationDimension.Effort, "The resolved Claude command does not advertise --effort."));
        return Task.FromResult<SessionConfigurationOutcome>(ConfigurationState?.Selection == configuration
            ? new SessionConfigurationOutcome.Applied(ConfigurationState)
            : new SessionConfigurationOutcome.RequiresReopen("Claude configuration requires a new process."));
    }

    public Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct) =>
        Task.FromResult(SteerDelivery.Declined);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _onDispose?.Invoke(this);
        var active = Interlocked.Exchange(ref _currentTurn, null);
        active?.CompleteQuietly();

        try
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
            await _process.DisposeAsync().ConfigureAwait(false);

            try
            {
                await _pumpTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore pump shutdown exception.
            }
        }
        finally
        {
            TryDeleteTempFile(_mcpConfigFilePath);
            _turnLock.Dispose();
            _pumpCts.Dispose();
        }
    }

    private async Task PumpNdjsonStdoutAsync()
    {
        Exception? pumpFailure = null;
        try
        {
            while (!_pumpCts.IsCancellationRequested)
            {
                var line = await _process.ReadStdoutLineAsync(_pumpCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var trimmed = line.AsSpan().TrimStart();
                if (trimmed.IsEmpty || trimmed[0] != '{')
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    HandleNdjsonFrame(doc.RootElement);
                }
            }
        }
        catch (OperationCanceledException) when (_pumpCts.IsCancellationRequested)
        {
            // Clean shutdown.
        }
        catch (Exception ex)
        {
            pumpFailure = ex;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var stderr = _process.GetStderrSummary();
        var message = pumpFailure?.Message
                      ?? (string.IsNullOrWhiteSpace(stderr)
                          ? "Claude stream-json stdout closed before turn result."
                          : $"Claude process exited unexpectedly: {stderr}");
        // Publish the fence before clearing confirmation or stopping the child. A new turn
        // must never write to a process whose sole stdout reader has terminated.
        Volatile.Write(ref _terminalReaderFailure, message);
        ConfigurationState = null;
        var failure = pumpFailure as SessionConfigurationException
            ?? new SessionConfigurationException(new SessionConfigurationOutcome.Rejected(
                ConfigurationFailureCode.OperationFailed, message, new(null, null, false)));
        _initialized.TrySetException(failure);
        _identityObserved.TrySetException(failure);
        await _process.DisposeAsync().ConfigureAwait(false);
        var turnState = Volatile.Read(ref _currentTurn);
        if (turnState is null)
        {
            return;
        }

        if (turnState.CancelRequested)
        {
            turnState.TryFinish(new UnifiedAgentEvent.TurnError(
                "turn_interrupted",
                "Claude turn was interrupted via SIGINT.",
                IsRetryable: false));
            return;
        }

        turnState.TryFinish(new UnifiedAgentEvent.TurnError("claude_disconnected", message, IsRetryable: false));
    }

    private void HandleNdjsonFrame(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var sessionId = TryGetString(root, "session_id") ?? TryGetString(root, "sessionId");
        var turnState = Volatile.Read(ref _currentTurn);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            BindSessionId(sessionId, turnState);
        }

        var type = TryGetString(root, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        switch (type)
        {
            case "control_response":
                if (root.TryGetProperty("response", out var response) &&
                    response.ValueKind == JsonValueKind.Object && TryGetString(response, "request_id") == _initializeRequestId)
                {
                    if (TryGetString(response, "subtype") == "success") _initialized.TrySetResult();
                    else _initialized.TrySetException(new SessionConfigurationException(new SessionConfigurationOutcome.Rejected(
                        ConfigurationFailureCode.InvalidAcknowledgement, "Claude rejected initialization.", new(null, null, false))));
                }
                break;
            case "system":
            {
                var sysSubtype = TryGetString(root, "subtype");
                if (string.Equals(sysSubtype, "init", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("slash_commands", out var slashCmds) &&
                    slashCmds.ValueKind == JsonValueKind.Array)
                {
                    var hasCompact = false;
                    foreach (var cmd in slashCmds.EnumerateArray())
                    {
                        if (cmd.ValueKind == JsonValueKind.String &&
                            string.Equals(cmd.GetString(), "compact", StringComparison.OrdinalIgnoreCase))
                        {
                            hasCompact = true;
                            break;
                        }
                    }

                    Volatile.Write(ref _supportsNativeCompact, hasCompact);
                }

                break;
            }

            case "stream_event":
            {
                if (turnState is not null &&
                    root.TryGetProperty("event", out var innerEvt) &&
                    innerEvt.ValueKind == JsonValueKind.Object)
                {
                    HandleStreamEventPayload(innerEvt, turnState);
                }

                break;
            }

            case "content_block_delta":
            case "message_stop":
            {
                if (turnState is not null)
                {
                    HandleStreamEventPayload(root, turnState);
                }

                break;
            }

            case "text_delta":
            {
                var delta = TryGetString(root, "delta") ?? TryGetString(root, "text");
                if (turnState is not null && !string.IsNullOrEmpty(delta))
                {
                    turnState.SawStreamedText = true;
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.TextDelta(delta));
                }

                break;
            }

            case "thinking_delta":
            {
                var delta = TryGetString(root, "delta") ?? TryGetString(root, "thinking") ?? TryGetString(root, "text");
                if (turnState is not null && !string.IsNullOrEmpty(delta))
                {
                    turnState.SawStreamedThinking = true;
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ThinkingDelta(delta));
                }

                break;
            }

            case "assistant":
            {
                if (turnState is not null)
                {
                    if (root.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.Object &&
                        TryParseClaudeUsage(message) is { } usage)
                    {
                        turnState.LatestAssistantUsage = usage;
                        if (usage.ContextWindowTokens is { } window)
                        {
                            turnState.ContextWindowTokens = window;
                            turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ContextUsageUpdated(usage));
                        }
                    }
                    HandleAssistantMessage(root, turnState);
                }

                break;
            }

            case "user":
            {
                if (turnState is not null)
                {
                    HandleUserToolResultMessage(root, turnState);
                }

                break;
            }

            case "tool_use":
            {
                if (turnState is not null)
                {
                    EmitToolUseBlock(root, turnState);
                }

                break;
            }

            case "tool_result":
            {
                if (turnState is not null)
                {
                    EmitToolResultBlock(root, turnState);
                }

                break;
            }

            case "control_request":
            case "approval_request":
            case "permission_request":
            {
                HandleApprovalRequest(root);
                break;
            }

            case "result":
            {
                if (turnState is not null)
                {
                    HandleResultFrame(root, turnState);
                }

                break;
            }

            case "error":
            {
                if (turnState is not null)
                {
                    var code = TryGetString(root, "code") ?? "claude_error";
                    var message = TryGetString(root, "message")
                                  ?? (root.TryGetProperty("error", out var errObj)
                                      ? TryGetString(errObj, "message") ?? errObj.GetRawText()
                                      : "Claude reported an error.");
                    var retryable = TryGetBool(root, "is_retryable")
                                    ?? TryGetBool(root, "retryable")
                                    ?? false;
                    turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, message, retryable));
                }

                break;
            }
        }
    }

    private void BindSessionId(string sessionId, TurnStreamState? turnState)
    {
        if (_resumeNativeThreadId is not null && sessionId != _resumeNativeThreadId)
            throw new SessionConfigurationException(new SessionConfigurationOutcome.Rejected(
                ConfigurationFailureCode.InvalidAcknowledgement, "Claude returned a different resumed session identity.", new(null, null, false)));
        Volatile.Write(ref _observedNativeThreadId, sessionId);
        _identityObserved.TrySetResult();
        if (turnState is not null && !string.Equals(_lastEmittedThreadId, sessionId, StringComparison.Ordinal))
        {
            _lastEmittedThreadId = sessionId;
            turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ThreadBound(sessionId));
        }
    }

    private static void HandleStreamEventPayload(JsonElement eventObj, TurnStreamState turnState)
    {
        var evtType = TryGetString(eventObj, "type");
        if (string.Equals(evtType, "content_block_delta", StringComparison.Ordinal) &&
            eventObj.TryGetProperty("delta", out var deltaObj) &&
            deltaObj.ValueKind == JsonValueKind.Object)
        {
            var deltaType = TryGetString(deltaObj, "type");
            if (string.Equals(deltaType, "text_delta", StringComparison.Ordinal))
            {
                var text = TryGetString(deltaObj, "text") ?? TryGetString(deltaObj, "delta");
                if (!string.IsNullOrEmpty(text))
                {
                    turnState.SawStreamedText = true;
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.TextDelta(text));
                }
            }
            else if (string.Equals(deltaType, "thinking_delta", StringComparison.Ordinal))
            {
                var thinking = TryGetString(deltaObj, "thinking") ?? TryGetString(deltaObj, "delta") ?? TryGetString(deltaObj, "text");
                if (!string.IsNullOrEmpty(thinking))
                {
                    turnState.SawStreamedThinking = true;
                    turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ThinkingDelta(thinking));
                }
            }
        }
        else if (string.Equals(evtType, "message_stop", StringComparison.Ordinal))
        {
            // Keep SawStreamed* until the matching assistant summary frame arrives (if any).
        }
    }

    private static void HandleAssistantMessage(JsonElement root, TurnStreamState turnState)
    {
        JsonElement contentArray = default;
        var hasContent = (root.TryGetProperty("message", out var msgObj) &&
                          msgObj.ValueKind == JsonValueKind.Object &&
                          msgObj.TryGetProperty("content", out contentArray) &&
                          contentArray.ValueKind == JsonValueKind.Array) ||
                         (root.TryGetProperty("content", out contentArray) &&
                          contentArray.ValueKind == JsonValueKind.Array);

        if (hasContent)
        {
            foreach (var block in contentArray.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var blockType = TryGetString(block, "type");
                switch (blockType)
                {
                    case "thinking":
                    {
                        if (!turnState.SawStreamedThinking)
                        {
                            var thinking = TryGetString(block, "thinking") ?? TryGetString(block, "text");
                            if (!string.IsNullOrEmpty(thinking))
                            {
                                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ThinkingDelta(thinking));
                            }
                        }

                        break;
                    }

                    case "text":
                    {
                        if (!turnState.SawStreamedText)
                        {
                            var text = TryGetString(block, "text");
                            if (!string.IsNullOrEmpty(text))
                            {
                                turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.TextDelta(text));
                            }
                        }

                        break;
                    }

                    case "tool_use":
                    {
                        EmitToolUseBlock(block, turnState);
                        break;
                    }
                }
            }
        }

        turnState.SawStreamedText = false;
        turnState.SawStreamedThinking = false;
    }

    private static void EmitToolUseBlock(JsonElement block, TurnStreamState turnState)
    {
        var id = TryGetString(block, "id") ?? TryGetString(block, "call_id") ?? Guid.NewGuid().ToString("N");
        if (!turnState.StartedToolIds.Add(id))
        {
            return;
        }

        var name = TryGetString(block, "name") ?? TryGetString(block, "tool_name") ?? "tool";
        var args = block.TryGetProperty("input", out var inputProp) &&
                   inputProp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? inputProp.Clone()
            : block.TryGetProperty("arguments", out var argsProp) &&
              argsProp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? argsProp.Clone()
                : JsonRpcStdioConnection.CreateEmptyJsonObject();

        turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallStarted(id, name, args));
    }

    private static void HandleUserToolResultMessage(JsonElement root, TurnStreamState turnState)
    {
        JsonElement contentArray = default;
        var hasContent = (root.TryGetProperty("message", out var msgObj) &&
                          msgObj.ValueKind == JsonValueKind.Object &&
                          msgObj.TryGetProperty("content", out contentArray) &&
                          contentArray.ValueKind == JsonValueKind.Array) ||
                         (root.TryGetProperty("content", out contentArray) &&
                          contentArray.ValueKind == JsonValueKind.Array);

        if (!hasContent)
        {
            return;
        }

        foreach (var block in contentArray.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object &&
                string.Equals(TryGetString(block, "type"), "tool_result", StringComparison.Ordinal))
            {
                EmitToolResultBlock(block, turnState);
            }
        }
    }

    private static void EmitToolResultBlock(JsonElement block, TurnStreamState turnState)
    {
        var callId = TryGetString(block, "tool_use_id")
                     ?? TryGetString(block, "call_id")
                     ?? TryGetString(block, "id")
                     ?? string.Empty;
        var isError = TryGetBool(block, "is_error") ?? TryGetBool(block, "isError") ?? false;
        var output = ExtractToolResultText(block);
        turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ToolCallCompleted(callId, output, isError));
    }

    private void HandleApprovalRequest(JsonElement root)
    {
        var nativeRequestId = TryGetString(root, "request_id")
                        ?? TryGetString(root, "requestId")
                        ?? TryGetString(root, "id")
                        ?? Guid.NewGuid().ToString("N");

        var sourceObj = root.TryGetProperty("request", out var reqObj) && reqObj.ValueKind == JsonValueKind.Object
            ? reqObj
            : root;

        var toolName = TryGetString(sourceObj, "tool_name")
                       ?? TryGetString(sourceObj, "toolName")
                       ?? TryGetString(sourceObj, "name")
                       ?? "tool";

        var inputElement = sourceObj.TryGetProperty("input", out var inProp) &&
                           inProp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? inProp.Clone()
            : sourceObj.TryGetProperty("arguments", out var argProp) &&
              argProp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? argProp.Clone()
                : JsonRpcStdioConnection.CreateEmptyJsonObject();

        var requestId = $"permission_{Guid.NewGuid():N}";
        _pendingApprovals[requestId] = new PendingClaudeApproval(nativeRequestId, inputElement);
        var currentTurn = Volatile.Read(ref _currentTurn);
        if (currentTurn is null || currentTurn.CancelRequested)
        {
            if (_pendingApprovals.TryRemove(requestId, out var cancelled))
            {
                _ = TryWriteApprovalResponseAsync(cancelled, approved: false, reason: "Turn was interrupted.", CancellationToken.None);
            }

            return;
        }

        currentTurn.PendingApprovalIds[requestId] = 0;
        if (!currentTurn.Events.Writer.TryWrite(new UnifiedAgentEvent.ApprovalRequested(requestId, toolName, inputElement)))
        {
            if (_pendingApprovals.TryRemove(requestId, out var cancelled))
            {
                _ = TryWriteApprovalResponseAsync(cancelled, approved: false, reason: "Turn was interrupted.", CancellationToken.None);
            }
        }
    }

    private void HandleResultFrame(JsonElement root, TurnStreamState turnState)
    {
        var resultUsage = TryParseClaudeUsage(root);
        var window = TryExtractModelUsageContextWindow(root)
                     ?? resultUsage?.ContextWindowTokens
                     ?? turnState.ContextWindowTokens;
        var usage = turnState.LatestAssistantUsage ?? resultUsage;
        if (usage is not null)
        {
            usage = usage with { ContextWindowTokens = window };
            turnState.Events.Writer.TryWrite(new UnifiedAgentEvent.ContextUsageUpdated(usage));
        }
        var isError = TryGetBool(root, "is_error") ?? TryGetBool(root, "isError") ?? false;
        var subtype = TryGetString(root, "subtype");

        if (subtype is not null &&
            (string.Equals(subtype, "interrupted", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(subtype, "cancelled", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(subtype, "canceled", StringComparison.OrdinalIgnoreCase)))
        {
            turnState.TryFinish(new UnifiedAgentEvent.TurnInterrupted());
            return;
        }

        if (isError || (subtype is not null && subtype.StartsWith("error", StringComparison.OrdinalIgnoreCase)))
        {
            var code = string.IsNullOrWhiteSpace(subtype) ? "claude_turn_error" : subtype;
            var msg = TryGetString(root, "error")
                      ?? TryGetString(root, "result")
                      ?? ExtractErrorsList(root)
                      ?? "Claude turn failed.";
            var retryable = TryGetBool(root, "is_retryable") ?? TryGetBool(root, "retryable") ?? false;
            turnState.TryFinish(new UnifiedAgentEvent.TurnError(code, msg, retryable, IsConclusive: true));
            return;
        }

        turnState.TryFinish(new UnifiedAgentEvent.TurnCompleted(usage));
    }

    private static TokenUsage? TryParseClaudeUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageObj) || usageObj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = TryGetLong(usageObj, "input_tokens") ?? TryGetLong(usageObj, "inputTokens");
        var output = TryGetLong(usageObj, "output_tokens") ?? TryGetLong(usageObj, "outputTokens");
        if (input is null && output is null)
        {
            return null;
        }

        var cacheRead = TryGetLong(usageObj, "cache_read_input_tokens")
                        ?? TryGetLong(usageObj, "cached_input_tokens")
                        ?? TryGetLong(usageObj, "cachedInputTokens")
                        ?? 0L;
        var window = TryGetLong(usageObj, "context_window_tokens")
                     ?? TryGetLong(usageObj, "contextWindowTokens")
                     ?? TryExtractModelUsageContextWindow(root);

        var cacheCreation = TryGetLong(usageObj, "cache_creation_input_tokens") ?? 0L;
        return new TokenUsage(input ?? 0L, output ?? 0L, cacheRead, window,
            (input ?? 0L) + cacheCreation + cacheRead + (output ?? 0L));
    }

    private static long? TryExtractModelUsageContextWindow(JsonElement root)
    {
        if (!root.TryGetProperty("modelUsage", out var modelUsage) || modelUsage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in modelUsage.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                var window = TryGetLong(prop.Value, "contextWindow")
                             ?? TryGetLong(prop.Value, "context_window");
                if (window is not null)
                {
                    return window;
                }
            }
        }

        return null;
    }

    private static string ExtractToolResultText(JsonElement block)
    {
        if (block.TryGetProperty("content", out var contentProp))
        {
            if (contentProp.ValueKind == JsonValueKind.String)
            {
                return contentProp.GetString() ?? string.Empty;
            }

            if (contentProp.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var elem in contentProp.EnumerateArray())
                {
                    if (elem.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }

                        sb.Append(elem.GetString());
                    }
                    else if (elem.ValueKind == JsonValueKind.Object)
                    {
                        var text = TryGetString(elem, "text");
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (sb.Length > 0)
                            {
                                sb.Append('\n');
                            }

                            sb.Append(text);
                        }
                    }
                }

                return sb.ToString();
            }

            if (contentProp.ValueKind == JsonValueKind.Object)
            {
                return contentProp.GetRawText();
            }
        }

        return TryGetString(block, "output") ?? string.Empty;
    }

    private static string? ExtractErrorsList(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errorsProp) || errorsProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = new List<string>();
        foreach (var err in errorsProp.EnumerateArray())
        {
            if (err.ValueKind == JsonValueKind.String)
            {
                var s = err.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    items.Add(s);
                }
            }
            else if (err.ValueKind == JsonValueKind.Object)
            {
                var s = TryGetString(err, "message") ?? err.GetRawText();
                items.Add(s);
            }
        }

        return items.Count > 0 ? string.Join("; ", items) : null;
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore temp file cleanup errors.
        }
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
}
