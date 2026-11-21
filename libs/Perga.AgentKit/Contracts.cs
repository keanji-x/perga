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
    string? ReasoningEffort = null);

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
    [property: System.Text.Json.Serialization.JsonIgnore] SessionConfigurationCapabilities? ConfigurationCapabilities = null);

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

public interface IAgentSession : IAsyncDisposable
{
    /// <summary>Runs the protocol handshake. The caller retains ownership if initialization fails.</summary>
    Task InitializeAsync(CancellationToken ct);
    IReadOnlyList<PermissionModeOption> AvailablePermissionModes => [];
    string? NativeThreadId { get; }
    bool SupportsNativeSteer { get; }
    long? ContextUsageRevision => 0;
    bool CanReuseContextWindow => false;
    bool SupportsNativeCompact => false;
    ValueTask<bool> ResolveNativeCompactSupportAsync(CancellationToken ct) =>
        ValueTask.FromResult(SupportsNativeCompact);
    IReadOnlyList<IdleContextUsage> AttachIdleContextUsageHandler(Action<IdleContextUsage>? handler) => [];
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
    Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct);
}

public interface IAgentRuntime : IAsyncDisposable
{
    bool CanResumeSession(string provider) => false;
    Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct);
    /// <summary>Starts and returns an owned session before initialization; the caller must dispose it.</summary>
    IAgentSession CreateSession(HarnessLaunchSpec spec);
}
