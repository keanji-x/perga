using System.Text.Json;

namespace Perga.AgentKit;

public abstract record McpServerConfig
{
    public sealed record HttpMcpServer(
        string Url,
        IReadOnlyDictionary<string, string> Headers) : McpServerConfig;

    public sealed record StdioMcpServer(
        string Command,
        IReadOnlyList<string> Args,
        IReadOnlyDictionary<string, string> Env) : McpServerConfig;
}

public sealed record HarnessLaunchSpec(
    string Provider,   // "codex" | "claude" | "acp:antigravity" | "acp:opencode" | "oar:<runtime>"
    string? Model,
    string Cwd,
    string? ResumeNativeThreadId,
    string? SystemPrompt,
    IReadOnlyDictionary<string, McpServerConfig> McpServers,
    string? ReasoningEffort = null,
    string? StateKey = null,
    string? PermissionMode = null);

public sealed record TurnInput(
    string Prompt,
    string CorrelationId,
    string? Model = null,
    string? ReasoningEffort = null,
    IReadOnlyList<TurnImage>? Attachments = null);

public sealed record TurnImage(string ContentType, int Size, Func<CancellationToken, Task<Stream?>> OpenAsync);

/// <summary>A deterministic refusal before calling the provider prompt or steer operation.</summary>
public sealed class AttachmentDispatchException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool BeforePrompt => true;

    public static AttachmentDispatchException Unsupported() =>
        new("attachments_unsupported", "The provider session does not support image attachments.");
}

public sealed record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long? ContextWindowTokens,
    long? UsedTokens = null)
{
    public long EffectiveUsedTokens => UsedTokens ?? InputTokens + OutputTokens;

    public void Deconstruct(out long inputTokens, out long outputTokens,
        out long cachedInputTokens, out long? contextWindowTokens)
    {
        inputTokens = InputTokens;
        outputTokens = OutputTokens;
        cachedInputTokens = CachedInputTokens;
        contextWindowTokens = ContextWindowTokens;
    }
}

public sealed record ApprovalOption(
    string OptionId,
    string Label,
    bool IsApproval);

public abstract record UnifiedAgentEvent
{
    public sealed record TaskStarted(AgentRuntimeTask Task) : UnifiedAgentEvent;
    public sealed record TaskUpdated(AgentRuntimeTask Task) : UnifiedAgentEvent;
    public sealed record TaskEnded(AgentRuntimeTask Task) : UnifiedAgentEvent;
    public sealed record AgentStatusChanged(AgentRuntimeStatus Status) : UnifiedAgentEvent;
    public sealed record McpServerStatusChanged(string Server, string Status, string? Error = null) : UnifiedAgentEvent;
    public sealed record RuntimeExited : UnifiedAgentEvent;
    public sealed record TurnAccepted(string? NativeTurnId) : UnifiedAgentEvent;
    public sealed record ThreadBound(string NativeThreadId) : UnifiedAgentEvent;
    public sealed record TextDelta(string Delta) : UnifiedAgentEvent;
    public sealed record ThinkingDelta(string Delta) : UnifiedAgentEvent;
    public sealed record ToolCallStarted(string CallId, string Name, JsonElement Args) : UnifiedAgentEvent;
    public sealed record ToolCallCompleted(string CallId, string Output, bool IsError) : UnifiedAgentEvent;
    public sealed record ApprovalRequested(
        string RequestId,
        string ToolName,
        JsonElement Args,
        IReadOnlyList<ApprovalOption>? Options = null) : UnifiedAgentEvent;
    public sealed record TurnInterrupted : UnifiedAgentEvent;
    public sealed record ContextUsageUpdated(TokenUsage Usage, long? ContextUsageRevision = 0) : UnifiedAgentEvent;
    public sealed record TurnCompleted(TokenUsage? Usage, long? ContextUsageRevision = 0, long? CompletedPromptId = null) : UnifiedAgentEvent;
    public sealed record TurnError(string Code, string Message, bool IsRetryable, bool IsConclusive = false, bool BeforePrompt = false) : UnifiedAgentEvent;
}

public sealed record ProviderModelEffort(
    string ReasoningEffort,
    string Description);

public sealed record ProviderModelInfo(
    string Model,
    string DisplayName,
    string Description,
    bool IsDefault,
    string? DefaultReasoningEffort,
    IReadOnlyList<ProviderModelEffort> SupportedReasoningEfforts);

public sealed record PermissionModeOption(
    string Id,
    string Label,
    string Description,
    string Tone = "neutral",
    bool IsDefault = false);

public sealed record ProviderStatus(
    string Provider,
    bool Available,
    string? Version,
    string? Reason,
    IReadOnlyList<string> Models,
    IReadOnlyList<ProviderModelInfo>? ModelCatalog = null,
    IReadOnlyList<PermissionModeOption>? PermissionModes = null,
    [property: System.Text.Json.Serialization.JsonIgnore] SessionConfigurationCapabilities? ConfigurationCapabilities = null,
    [property: System.Text.Json.Serialization.JsonIgnore] bool? SupportsImages = null);

public sealed record NativeCompactResult(
    TokenUsage? Usage = null,
    string? Summary = null,
    long? ContextUsageRevision = 0,
    long? CompletedPromptId = null);

public sealed record IdleContextUsage(
    TokenUsage Usage,
    long? ContextUsageRevision,
    string? NativeThreadId,
    long? CompletedPromptId = null);

public enum SteerDelivery { Accepted, Declined, Unconfirmed }

public sealed record AgentRuntimeTask(string TaskId, string? TaskType = null, string? NativeType = null,
    string? Description = null, string? ToolCallId = null, string? ChildSessionId = null,
    bool? Background = null, bool? Ambient = null, string? Status = null, string? Error = null,
    string? Summary = null, string? OutputFile = null, long? Seq = null, long? Ts = null);

public sealed record AgentIdleActivity(long Since, long LastEventAt);
public sealed record AgentTurnOutcome(string Kind, string? Failure = null, string? Reason = null,
    int? Status = null, string? Credential = null, string? ResetsAt = null, string? RateLimitType = null,
    JsonElement? StructuredOutput = null);
public sealed record AgentStopEvidence(IReadOnlyList<string> PendingAbortIds, bool AbortedOnExit);
public sealed record AgentCompaction(string RequestId, bool? Started = null);
public sealed record AgentPendingPrompt(string InputId, long SinceSeq, string? RequestId = null,
    AgentStopEvidence? Stop = null);
public sealed record AgentRuntimeStatus(string Kind, string? Phase = null, string? ToolName = null,
    string? ToolCallId = null, bool? Writing = null, long? SinceSeq = null, string? RequestId = null,
    string? InputId = null, bool? Released = null, AgentTurnOutcome? LastTurnOutcome = null,
    AgentIdleActivity? IdleActivity = null, AgentPendingPrompt? PendingPrompt = null,
    AgentStopEvidence? Stop = null, AgentCompaction? Compaction = null);

public interface IAgentSession : IAsyncDisposable
{
    /// <summary>Runs the protocol handshake. The caller retains ownership if initialization fails.</summary>
    Task InitializeAsync(CancellationToken ct);
    IReadOnlyList<PermissionModeOption> AvailablePermissionModes => [];
    string? NativeThreadId { get; }
    bool SupportsNativeSteer { get; }
    bool SupportsImages => false;
    bool ExpectsKernelMcpHandshake => false;
    long? ContextUsageRevision => 0;
    bool CanReuseContextWindow => false;
    bool SupportsNativeCompact => false;
    ValueTask<bool> ResolveNativeCompactSupportAsync(CancellationToken ct) =>
        ValueTask.FromResult(SupportsNativeCompact);
    IReadOnlyList<IdleContextUsage> AttachIdleContextUsageHandler(Action<IdleContextUsage>? handler) => [];
    // Output delivered through this handler belongs to the runtime outside its prompt stream.
    IReadOnlyList<UnifiedAgentEvent> AttachRuntimeActivityHandler(Action<UnifiedAgentEvent>? handler) => [];
    WorkerProviderConfiguration? WorkerConfiguration => null;
    bool ShutdownConfirmed => true;
    bool SupportsSessionResume => false;
    bool RequiresTerminalStop => false;
    bool SupportsScopedClose => false;
    Task<bool> TryCloseSessionAsync(CancellationToken ct) => Task.FromResult(false);
    Task<NativeCompactResult> CompactAsync(CancellationToken ct) =>
        throw new NotSupportedException("This agent session does not support native compaction.");

    IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(TurnInput input, CancellationToken ct);
    Task CancelTurnAsync(CancellationToken ct);
    Task RespondApprovalAsync(string requestId, bool approved, string? reason = null, CancellationToken ct = default) =>
        RespondApprovalAsync(requestId, approved, selectedOptionId: null, reason, ct);
    Task RespondApprovalAsync(
        string requestId,
        bool approved,
        string? selectedOptionId,
        string? reason = null,
        CancellationToken ct = default) =>
        (TryClaimApprovalResponse(requestId, approved, selectedOptionId, reason, ct)
            ?? throw new InvalidOperationException($"No pending approval request with id '{requestId}'."))();
    Func<Task>? TryClaimApprovalResponse(
        string requestId, bool approved, string? selectedOptionId, string? reason, CancellationToken ct);
    SessionConfigurationCapabilities ConfigurationCapabilities { get; }
    ConfirmedConfiguration? ConfigurationState { get; }
    Task<SessionConfigurationOutcome> ConfigureAsync(SessionConfiguration configuration, CancellationToken ct = default);
    // The adapter runs beforeWrite at its transport write boundary and propagates rejection.
    Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct, Func<CancellationToken, Task>? beforeWrite = null);
    Task<SteerDelivery> SteerAsync(TurnInput input, CancellationToken ct, Func<CancellationToken, Task>? beforeWrite = null) =>
        input.Attachments is { Count: > 0 }
            ? Task.FromException<SteerDelivery>(AttachmentDispatchException.Unsupported())
            : SteerAsync(input.CorrelationId, input.Prompt, ct, beforeWrite);
}

public interface IAgentRuntime : IAsyncDisposable
{
    bool CanResumeSession(string provider) => false;
    bool? GetImageSupport(string provider) => null;
    Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct);
    /// <summary>Starts and returns an owned session before initialization; the caller must dispose it.</summary>
    IAgentSession CreateSession(HarnessLaunchSpec spec);
}
