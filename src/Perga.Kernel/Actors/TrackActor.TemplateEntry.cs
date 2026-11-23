using Perga.Contracts;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    internal const string TemplateSelectorTitle = "模板选择";
    internal const string TemplateSelectorRequestKey = "template-selector:v1";

    public Task<TrackDto> ApplyTemplateByHumanAsync(ApplyTrackTemplateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot set a template on a closed track.");

            var result = await ApplyTemplateSelectionCoreAsync(
                callerSessionId: null,
                templateId: request.TemplateId,
                source: request.Source,
                workspaceSource: null,
                path: null,
                inputs: request.Inputs,
                dryRun: false,
                clear: false,
                resetData: request.ResetData,
                seedReport: request.SeedReport,
                authorization: null,
                ct: ct).ConfigureAwait(false);
            return result.Track;
        }, ct);
    }

    public async Task<(SessionCreationReceipt Receipt, bool Replay)?> StartTemplateSelectorAsync(
        StartTemplateSelectorRequest request,
        CancellationToken ct = default,
        InteropPrincipal? principal = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UserMessage))
            throw KernelException.InvalidArgument("userMessage is required.");

        var trimmedMessage = request.UserMessage.Trim();
        var prep = await EnqueueAsync<(bool Skip, AgentSessionDto? Existing, AgentSessionDto? Lead)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot start template selector on a closed track.");

            var existing = _sessions.Values.FirstOrDefault(s =>
                !s.IsLead &&
                s.ArchivedAtMs is null &&
                string.Equals(s.Title, TemplateSelectorTitle, StringComparison.Ordinal));
            if (existing is not null)
                return (false, existing, null);

            if (!_options.EnableTemplateSelectorSubagent ||
                _track.TemplateId is not null ||
                _templateCatalog.ListManifests().Count == 0)
            {
                return (true, null, null);
            }

            AgentSessionDto? lead = _track.LeadSessionId is { } leadId && _sessions.TryGetValue(leadId, out var foundLead)
                ? foundLead
                : _sessions.Values.FirstOrDefault(s => s.IsLead);
            return (false, null, lead);
        }, ct).ConfigureAwait(false);

        if (prep.Skip)
            return null;
        if (prep.Existing is not null)
            return (new SessionCreationReceipt(prep.Existing, null, TemplateSelectorRequestKey), true);

        var createRequest = new CreateSessionRequest(
            Title: TemplateSelectorTitle,
            Provider: prep.Lead?.Provider,
            Model: prep.Lead?.Model,
            ReasoningEffort: prep.Lead?.ReasoningEffort,
            PermissionMode: prep.Lead?.PermissionMode,
            Prompt: BuildTemplateSelectorPrompt(TrackId, trimmedMessage),
            NotifyParent: "none");

        return await CreateConversationAsync(createRequest, TemplateSelectorRequestKey, ct, principal).ConfigureAwait(false);
    }

    private static string BuildTemplateSelectorPrompt(string trackId, string userMessage) =>
        $"""
        你只负责为当前 Track（{trackId}）评估是否需要套用模板，不执行其他任务。

        用户首句：
        {userMessage}

        步骤：
        1. 调用 `template_list` 查看可用模板；如有潜在匹配，再调用 `template_read` 确认适用场景、输入参数与结构。
        2. 只有在需求明确契合某个模板的报告结构、数据模型或流程时，才调用 `mail_send`（`toTrackId: "{trackId}"`）通知当前 Track 的 Lead，只写清推荐的 `templateId`、建议的 `inputs`（如有）以及一句理由，由 Lead 决定是否调用 `track_set`。
        3. 如果需求只是简单问答、一次性查询或没有明确匹配的模板，不要调用 `mail_send`，直接回复 `NO_TEMPLATE` 并结束。
        4. 不要调用 `track_set`、`report_commit`、`flow_commit` 或创建其他会话。
        """;
}
