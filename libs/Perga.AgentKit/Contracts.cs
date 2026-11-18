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
    string? StateKey = null);

public sealed record TurnInput(
    string Prompt,
    string CorrelationId,
    string? Model = null,
    string? ReasoningEffort = null);

public sealed record WorkerConfigurationSupport(bool ModelOverride, IReadOnlyList<string> ReasoningEfforts)
{
    public static WorkerConfigurationSupport ForProvider(string provider) =>
        string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase)
            ? new(true, ["low", "medium", "high", "xhigh"])
            : new(false, []);
}

public sealed record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long? ContextWindowTokens);

public abstract record UnifiedAgentEvent
{
    public sealed record TurnAccepted(string? NativeTurnId) : UnifiedAgentEvent;
    public sealed record ThreadBound(string NativeThreadId) : UnifiedAgentEvent;
    public sealed record TextDelta(string Delta) : UnifiedAgentEvent;
    public sealed record ThinkingDelta(string Delta) : UnifiedAgentEvent;
    public sealed record ToolCallStarted(string CallId, string Name, JsonElement Args) : UnifiedAgentEvent;
    public sealed record ToolCallCompleted(string CallId, string Output, bool IsError) : UnifiedAgentEvent;
    public sealed record ApprovalRequested(string RequestId, string ToolName, JsonElement Args) : UnifiedAgentEvent;
    public sealed record TurnInterrupted : UnifiedAgentEvent;
    public sealed record TurnCompleted(TokenUsage? Usage) : UnifiedAgentEvent;
    public sealed record TurnError(string Code, string Message, bool IsRetryable, bool IsConclusive = false) : UnifiedAgentEvent;
}

public sealed record ProviderStatus(
    string Provider,
    bool Available,
    string? Version,
    string? Reason,
    IReadOnlyList<string> Models);

public enum SteerDelivery { Accepted, Declined, Unconfirmed }

public interface IAgentSession : IAsyncDisposable
{
    string? NativeThreadId { get; }
    bool SupportsNativeSteer { get; }
    bool ShutdownConfirmed => true;
    bool SupportsSessionResume => false;
    bool RequiresTerminalStop => false;
    bool SupportsScopedClose => false;
    Task<bool> TryCloseSessionAsync(CancellationToken ct) => Task.FromResult(false);

    IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(TurnInput input, CancellationToken ct);
    Task CancelTurnAsync(CancellationToken ct);
    Task RespondApprovalAsync(string requestId, bool approved, string? reason = null, CancellationToken ct = default);
    Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct);
}

public interface IAgentRuntime : IAsyncDisposable
{
    Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct);
    Task<IAgentSession> OpenSessionAsync(HarnessLaunchSpec spec, CancellationToken ct);
}
