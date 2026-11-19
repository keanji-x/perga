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
    IReadOnlyList<PermissionModeOption>? PermissionModes = null);

public enum SteerDelivery { Accepted, Declined, Unconfirmed }

public interface IAgentSession : IAsyncDisposable
{
    string? NativeThreadId { get; }
    bool SupportsNativeSteer { get; }
    WorkerProviderConfiguration? WorkerConfiguration => null;
    bool ShutdownConfirmed => true;
    bool SupportsSessionResume => false;
    bool RequiresTerminalStop => false;
    bool SupportsScopedClose => false;
    Task<bool> TryCloseSessionAsync(CancellationToken ct) => Task.FromResult(false);

    IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(TurnInput input, CancellationToken ct);
    Task CancelTurnAsync(CancellationToken ct);
    Task RespondApprovalAsync(string requestId, bool approved, string? reason = null, CancellationToken ct = default);
    Task<bool> TrySetModelAsync(string? model, string? reasoningEffort = null, CancellationToken ct = default);
    Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct);
}

public interface IAgentRuntime : IAsyncDisposable
{
    Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct);
    // Workspace probes require the owning session's effective isolation, even if discovery fails.
    // An empty catalogue means discovery is unavailable, not an authoritative rejection.
    async Task<IReadOnlyList<PermissionModeOption>> ProbePermissionModesAsync(string provider, string? cwd, OsSandboxMode sandbox, IReadOnlyList<OverlayMountSpec>? overlayMounts, CancellationToken ct) =>
        (await ProbeProvidersAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(status => string.Equals(status.Provider, provider, StringComparison.OrdinalIgnoreCase))?.PermissionModes ?? [];

    Task<IAgentSession> OpenSessionAsync(HarnessLaunchSpec spec, CancellationToken ct);
}
