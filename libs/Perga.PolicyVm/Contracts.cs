using System.Text.Json;

namespace Perga.PolicyVm;

public closed record GuardDecision
{
    public sealed record Allow : GuardDecision;
    public sealed record Deny(string Reason) : GuardDecision;
    public sealed record AskHuman(string Title, string ApproveLabel = "Approve", string DenyLabel = "Deny") : GuardDecision;
}

public sealed record DataValidationError(
    string Path,
    string Message);

public sealed record DataPatchValidationResult(
    bool Valid,
    JsonElement NextData,
    IReadOnlyList<DataValidationError> Errors);

public sealed record TemplateViewDiagnostic(
    string View,
    string Message);

public sealed record TemplateViewsProjection(
    string TemplateId,
    IReadOnlyDictionary<string, JsonElement> Views,
    IReadOnlyDictionary<string, string> Widgets,
    IReadOnlyList<TemplateViewDiagnostic> Diagnostics,
    long DataRev = 0,
    long FlowRev = 0,
    JsonElement? Flow = null);

public sealed record CompiledTemplateManifest(
    string Id,
    string Title,
    string? Description,
    IReadOnlyList<string> DefaultPlugins,
    JsonElement InputsSchema,
    bool RequireGateForReadWrite,
    string? WorkspaceDriver,
    IReadOnlyList<string> WorkspaceLowerCacheDirs,
    string? WorkspaceCreateCmd,
    string? WorkspaceCloseCmd,
    string? AfterWorkerDoneCmd,
    JsonElement Flow,
    IReadOnlyList<TaskRoutingRule> Routing,
    string? Extends = null,
    bool HasHeaderView = false,
    string InitialReportMarkdown = "",
    IReadOnlyList<string>? ViewNames = null);

public sealed record CompiledPolicy(
    string TemplateId,
    string JsSource,
    CompiledTemplateManifest Manifest);

public sealed record PolicyTurnPreparation(string? Context, GuardDecision Decision);

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
    string? BoundTaskId,
    double ContextUsageRatio,
    JsonElement Inputs);

public sealed record TaskRoutingWhen(string? Role, string? Access);
public sealed record TaskRoutingRule(TaskRoutingWhen When, string? Provider, string? Model, bool Enforce, string? TargetAccess = null);

public sealed record PolicyTaskRoute(string? Provider, string? Model, string? Denial = null, string? Access = null);

public interface IPolicyVm
{
    CompiledPolicy Compile(string jsModuleSource);
    GuardDecision Before(CompiledPolicy policy, string action, string trackId, JsonElement payload, JsonElement inputs, JsonElement data);
    TemplateHeaderView? EvaluateHeaderView(CompiledPolicy policy, PolicyHeaderViewContext ctx, JsonElement data);
    JsonElement InitData(CompiledPolicy policy, JsonElement inputs);
    IReadOnlyList<DataValidationError> ValidateData(CompiledPolicy policy, JsonElement inputs, JsonElement data, IReadOnlyList<string>? knownTaskKeys);
    PolicyTurnPreparation PrepareTurn(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement data);
    PolicyTaskRoute RouteTaskDispatch(CompiledPolicy policy, string? role, string access, string? provider, string? model, JsonElement inputs);
    IReadOnlyList<PolicyTaskRoute> ResolveRoutingTargets(CompiledPolicy policy, JsonElement inputs);
    IReadOnlyList<Perga.Contracts.FlowNodeSpecDto> ResolveFlow(CompiledPolicy policy, JsonElement inputs);
    void ValidateInputs(CompiledPolicy policy, JsonElement inputs);
    string InterpolateInputs(CompiledPolicy policy, string text, JsonElement inputs);
    DataPatchValidationResult ValidateAndPatchData(
        CompiledPolicy policy,
        JsonElement inputs,
        JsonElement currentData,
        JsonElement patch,
        IReadOnlyList<string>? knownTaskKeys,
        string nowIso,
        string source);
    TemplateViewsProjection ProjectViews(
        CompiledPolicy policy,
        JsonElement inputs,
        JsonElement currentData,
        JsonElement kernelSnapshot,
        IReadOnlySet<string>? viewNames = null);
}
