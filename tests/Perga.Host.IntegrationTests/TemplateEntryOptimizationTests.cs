using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class TemplateEntryOptimizationTests
{
    private const string EntryTemplateSource = """
        export default defineTemplate({
          id: "entry-delivery",
          title: "阶段交付",
          description: "按阶段推进研发交付",
          report: { initialMarkdown: "# 交付概览\n\n目标与验收标准已初始化。\n" },
          data: {
            initial: { phase: "design" }
          }
        });
        """;

    [Fact]
    public async Task HttpApplyTemplate_SeedsBlankReport_AndRejectsInvalidOrClosedTrack()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate(EntryTemplateSource);

        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Entry", DefaultCwd: host.DataDir));
        var created = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Blank track"));
        var trackId = created.Track.Id;

        using var invalidResp = await host.AuthenticatedClient.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/template",
            new { },
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResp.StatusCode);

        using var applyResp = await host.AuthenticatedClient.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/template",
            new ApplyTrackTemplateRequest(TemplateId: "entry-delivery"),
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.OK, applyResp.StatusCode);
        var updated = await applyResp.Content.ReadFromJsonAsync<TrackDto>(PergaJsonDefaults.ApiOptions);
        Assert.Equal("entry-delivery", updated!.TemplateId);
        Assert.Equal("design", updated.Data.GetProperty("phase").GetString());

        var report = await kernel.ReadReportAsync(trackId);
        Assert.Equal(1, report.DocRev);
        Assert.Contains("交付概览", report.RenderedMarkdown, StringComparison.Ordinal);

        await kernel.CloseTrackAsync(trackId, created.Track.LeadSessionId!);
        using var closedResp = await host.AuthenticatedClient.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/template",
            new ApplyTrackTemplateRequest(TemplateId: "entry-delivery"),
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.Conflict, closedResp.StatusCode);
    }

    [Fact]
    public async Task HttpTemplateSelector_CreatesForegroundSession_IsIdempotent_AndStaysSilentWithoutMail()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate(EntryTemplateSource);

        host.AgentRuntime.SetTurnHandler((spec, input, ct) => ReplyAsync(
            input.Prompt.Contains("NO_TEMPLATE", StringComparison.Ordinal) ? "NO_TEMPLATE" : "ok", ct));

        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Selector", DefaultCwd: host.DataDir));
        var created = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Quick question"));
        var trackId = created.Track.Id;

        using var firstResp = await host.AuthenticatedClient.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/template-selector",
            new StartTemplateSelectorRequest("1 + 1 等于几？"),
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.Created, firstResp.StatusCode);
        var firstReceipt = await firstResp.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions);
        Assert.NotNull(firstReceipt);
        Assert.Equal("模板选择", firstReceipt!.Session.Title);
        Assert.False(firstReceipt.Session.IsLead);

        using var replayResp = await host.AuthenticatedClient.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/template-selector",
            new StartTemplateSelectorRequest("1 + 1 等于几？"),
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);
        var replayReceipt = await replayResp.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions);
        Assert.Equal(firstReceipt.Session.Id, replayReceipt!.Session.Id);

        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));

        var snapshot = await kernel.GetTrackAggregateAsync(trackId);
        Assert.Equal(2, snapshot.Sessions.Count);
        Assert.Empty(await kernel.ListMailsAsync(trackId));
        Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns,
            turn => turn.Spec.StateKey == created.Track.LeadSessionId);

        var templated = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, TemplateId: "entry-delivery"));
        using var skippedResp = await host.AuthenticatedClient.PostAsJsonAsync(
            $"/api/v1/tracks/{templated.Track.Id}/template-selector",
            new StartTemplateSelectorRequest("再建一个有模板的"),
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.NoContent, skippedResp.StatusCode);
    }

    [Fact]
    public async Task HttpTemplateSelector_CannotCallTrackSetDirectly_AndCanNotifyLeadViaMail()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate(EntryTemplateSource);

        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("SelectorMail", DefaultCwd: host.DataDir));
        var created = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Feature delivery"));
        var trackId = created.Track.Id;

        var selectorFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leadWoken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        host.AgentRuntime.BeforeTurn = (input, _) =>
        {
            if (input.Prompt.Contains("NO_TEMPLATE", StringComparison.Ordinal))
            {
                selectorFinished.TrySetResult();
            }
            else if (input.Prompt.Contains("建议使用 entry-delivery 模板", StringComparison.Ordinal))
            {
                leadWoken.TrySetResult();
            }
            return Task.CompletedTask;
        };

        using var startResp = await host.AuthenticatedClient.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/template-selector",
            new StartTemplateSelectorRequest("按阶段推进新功能的研发与交付"),
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.Created, startResp.StatusCode);
        var receipt = await startResp.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions);
        await selectorFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));

        var selectorCaller = await kernel.OpenMcpCallerAsync(receipt!.Session.Id);

        var forbidden = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(
                kernel,
                selectorCaller,
                KernelMcpToolNames.TrackSet,
                JsonSerializer.SerializeToElement(new { templateId = "entry-delivery" })));
        Assert.Equal(KernelErrorCode.Forbidden, forbidden.Code);

        await KernelMcpDispatcher.ExecuteGuardedAsync(
            kernel,
            selectorCaller,
            KernelMcpToolNames.MailSend,
            JsonSerializer.SerializeToElement(new
            {
                toTrackId = trackId,
                summary = "模板建议：entry-delivery",
                body = "建议使用 entry-delivery 模板，因为该任务需要分阶段推进研发与交付。"
            }));

        await leadWoken.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));

        var mails = await kernel.ListMailsAsync(trackId);
        Assert.Single(mails);
        Assert.Equal("模板建议：entry-delivery", mails[0].Summary);
        Assert.Null((await kernel.GetTrackAggregateAsync(trackId)).Track.TemplateId);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> ReplyAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new UnifiedAgentEvent.TextDelta(text);
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(50, 20, 0, 100_000));
    }
}
