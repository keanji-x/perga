using System.Text.Json;

namespace Perga.PolicyVm;

public closed record GuardDecision
{
    public sealed record Allow : GuardDecision;
    public sealed record Deny(string Reason) : GuardDecision;
    public sealed record AskHuman(string Title, string ApproveLabel = "Approve", string DenyLabel = "Deny") : GuardDecision;
}

public sealed record SectionRule(
    string H1,
    bool OmitIfEmpty = false,
    IReadOnlyList<string>? EditableBy = null);

public sealed record ScheduleSpec(
    string Id,
    string Cron,
    string Timezone,
    string Prompt,
    string? Title = null);

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
    IReadOnlyList<TaskRoutingRule> Routing,
    WorkerDispatchDefaults? WorkerDispatch = null,
    string? Extends = null,
    bool HasHeaderView = false);

public sealed record CompiledPolicy(
    string TemplateId,
    string JsSource,
    CompiledTemplateManifest Manifest);

public sealed record PolicyTurnPreparation(string? Context, GuardDecision Decision);

public sealed record PolicyEvalResult<T>(
    JsonElement NextState,
    T Output);

public sealed record TemplateHeaderBadge(string Label, string? Tone = null);
public sealed record TemplateHeaderNotice(string Text, string? Tone = null);
public sealed record TemplateHeaderMetric(string Label, string Value, string? Delta = null,
    string? Tone = null, string? Sub = null, IReadOnlyList<double>? Spark = null);
public sealed record TemplateHeaderRule(string Label, string Text);
public sealed record TemplateHeaderView(
    IReadOnlyList<TemplateHeaderBadge>? Badges = null,
    TemplateHeaderNotice? Notice = null,
    IReadOnlyList<TemplateHeaderMetric>? Metrics = null,
    IReadOnlyList<TemplateHeaderRule>? Rules = null);

public sealed record PolicyHeaderViewContext(string TrackId, JsonElement Inputs);

public sealed record PolicyTurnContext(
    string TrackId,
    string SessionId,
    bool IsLead,
    string? BoundAttemptId,
    double ContextUsageRatio,
    JsonElement Inputs);

public sealed record TaskRoutingWhen(string? Role, string? Access);
public sealed record TaskRoutingRule(TaskRoutingWhen When, string? Provider, string? Model, bool Enforce);

public sealed record PolicyTaskRoute(string? Provider, string? Model, string? Denial = null);

public interface IPolicyVm
{
    CompiledPolicy Compile(string jsModuleSource);
    PolicyEvalResult<GuardDecision> Before(CompiledPolicy policy, string action, string trackId, JsonElement data, JsonElement inputs, JsonElement state);
    PolicyEvalResult<string?> After(CompiledPolicy policy, string eventName, string trackId, JsonElement data, JsonElement inputs, JsonElement state);
    TemplateHeaderView? EvaluateHeaderView(CompiledPolicy policy, PolicyHeaderViewContext ctx, JsonElement state);
    PolicyEvalResult<JsonElement> InitState(CompiledPolicy policy, JsonElement inputs);
    PolicyEvalResult<PolicyTurnPreparation> PrepareTurn(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement state);
    PolicyTaskRoute RouteTaskDispatch(CompiledPolicy policy, string? role, string access, string? provider, string? model, JsonElement inputs);
    IReadOnlyList<PolicyTaskRoute> ResolveRoutingTargets(CompiledPolicy policy, JsonElement inputs);
    void ValidateInputs(CompiledPolicy policy, JsonElement inputs);
    string InterpolateInputs(CompiledPolicy policy, string text, JsonElement inputs);
}
