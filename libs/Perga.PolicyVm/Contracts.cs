using System.Text.Json;

namespace Perga.PolicyVm;

public closed record GuardDecision
{
    public sealed record Allow : GuardDecision;
    public sealed record Deny(string Reason) : GuardDecision;
    public sealed record AskHuman(string Title, IReadOnlyList<string> Options) : GuardDecision;
}

public sealed record SectionRule(
    string H1,
    bool OmitIfEmpty = false,
    IReadOnlyList<string>? EditableBy = null);

public sealed record ScheduleSpec(
    string Id,
    string Cron,
    string Timezone,
    string? StaticWakePrompt = null);

public sealed record WorkerDispatchDefaults(string? Section, string? Access, string? GateInput);

public sealed record CompiledTemplateManifest(
    string Id,
    string Title,
    string? Description,
    IReadOnlyList<string> DefaultPlugins,
    JsonElement InputsSchema,
    IReadOnlyList<SectionRule> Sections,
    int? MaxProseChars,
    bool RequireGateForReadWrite,
    string? WorkspaceDriver,
    IReadOnlyList<string> WorkspaceLowerCacheDirs,
    string? WorkspaceCreateCmd,
    string? WorkspaceCloseCmd,
    string? AfterWorkerDoneCmd,
    IReadOnlyList<ScheduleSpec> Schedules,
    string InitialReportMarkdown,
    WorkerDispatchDefaults? WorkerDispatch = null);

public sealed record CompiledPolicy(
    string TemplateId,
    string JsSource,
    CompiledTemplateManifest Manifest);

public sealed record PolicyEvalResult<T>(
    JsonElement NextState,
    T Output);

public sealed record PolicyTurnContext(
    string TrackId,
    string SessionId,
    bool IsLead,
    string? BoundAttemptId,
    double ContextUsageRatio,
    JsonElement Inputs);

public sealed record PolicyReportCommitContext(
    string TrackId,
    string EditorRole,     // "human" | "lead" | "subagent" | "system"
    string PrevMarkdown,
    string NextMarkdown,
    IReadOnlyList<string> TouchedSections,
    int NextProseChars,
    JsonElement Inputs);

public sealed record PolicyToolCallContext(
    string TrackId,
    string SessionId,
    bool IsLead,
    string? BoundAttemptId,
    string McpServer,      // "perga" | "vcs" | "<pluginId>"
    string ToolName,
    JsonElement Arguments,
    JsonElement Inputs);

public sealed record PolicyTaskSettledContext(
    string TrackId,
    string TaskKey,
    string AccessMode,     // "read_only" | "read_write"
    string AttemptStatus,  // "done" | "failed" | "canceled"
    JsonElement? WorkerResult,
    JsonElement AttemptMeta,
    JsonElement? GateVerdict,
    JsonElement Inputs);

public interface IPolicyVm
{
    CompiledPolicy Compile(string jsModuleSource);
    PolicyEvalResult<JsonElement> InitState(CompiledPolicy policy, JsonElement inputs);
    PolicyEvalResult<string?> BuildTurnContext(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement state);
    PolicyEvalResult<GuardDecision> GuardReportCommit(CompiledPolicy policy, PolicyReportCommitContext ctx, JsonElement state);
    PolicyEvalResult<GuardDecision> GuardToolCall(CompiledPolicy policy, PolicyToolCallContext ctx, JsonElement state);
    PolicyEvalResult<string?> OnTaskSettled(CompiledPolicy policy, PolicyTaskSettledContext ctx, JsonElement state);
    PolicyEvalResult<string?> BuildScheduleWakePrompt(CompiledPolicy policy, string scheduleId, JsonElement inputs, JsonElement state);
}
