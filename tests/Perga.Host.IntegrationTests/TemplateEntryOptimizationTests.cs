using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;

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
}
