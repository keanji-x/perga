using System.Text.Json;

namespace Perga.AgentKit;

public enum OsSandboxMode
{
    None,
    WorkspaceWriteBwrap,
    ReadOnlyBwrap
}

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

public sealed record OverlayMountSpec(
    IReadOnlyList<string> LowerDirs,
    string? UpperDir,
    string? WorkDir,
    string TargetPath);

public sealed record HarnessLaunchSpec(
    string Provider,   // "codex" | "claude" | "acp:antigravity" | "acp:opencode"
    string? Model,
    string Cwd,
    string? ResumeNativeThreadId,
    string? SystemPrompt,
    IReadOnlyDictionary<string, McpServerConfig> McpServers,
    OsSandboxMode Sandbox = OsSandboxMode.None,
    IReadOnlyList<OverlayMountSpec>? OverlayMounts = null,
    string? ReasoningEffort = null,
    string? StateKey = null,
    bool ConfigureWorker = false,
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
    // CachedInputTokens is a subset of InputTokens unless a provider supplies
    // an explicit snapshot that accounts for its different cache semantics.
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
    // Options returned by this open provider session, including registered launch modes.
    IReadOnlyList<PermissionModeOption> AvailablePermissionModes => [];
    string? NativeThreadId { get; }
    bool SupportsNativeSteer { get; }
    // Revisions are local to this session instance. Null means the producing configuration is unconfirmed.
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
    Task RespondApprovalAsync(string requestId, bool approved, string? reason = null, CancellationToken ct = default);
    Task RespondApprovalAsync(
        string requestId,
        bool approved,
        string? selectedOptionId,
        string? reason = null,
        CancellationToken ct = default) =>
        RespondApprovalAsync(requestId, approved, reason, ct);
    // Sessions with a pending request registry claim the response in memory here.
    // A null result means that the request already ended.
    // Invoke the returned sender at most once, outside the execution lock.
    Func<Task>? TryClaimApprovalResponse(
        string requestId, bool approved, string? selectedOptionId, string? reason, CancellationToken ct) =>
        () => RespondApprovalAsync(requestId, approved, selectedOptionId, reason, ct);
    SessionConfigurationCapabilities ConfigurationCapabilities { get; }
    ConfirmedConfiguration? ConfigurationState { get; }
    Task<SessionConfigurationOutcome> ConfigureAsync(SessionConfiguration configuration, CancellationToken ct = default);
    Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct);
}

public interface IAgentRuntime : IAsyncDisposable
{
    Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct);
    Task<IAgentSession> OpenSessionAsync(HarnessLaunchSpec spec, CancellationToken ct);
}
