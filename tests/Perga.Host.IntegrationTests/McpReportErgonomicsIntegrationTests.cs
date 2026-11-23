using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Host.IntegrationTests;

public sealed class McpReportErgonomicsIntegrationTests
{
    [Fact]
    public async Task HistoricalCheckpointsStayReadableAcrossUnrelatedReportEdits()
    {
        await using var cluster = new PergaHostTestCluster();
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        var store = cluster.Services.GetRequiredService<ITrackStore>();
        var area = await kernel.CreateAreaAsync(new("Historical Report", DefaultCwd: cluster.DataDir));
        var track = (await kernel.CreateTrackAsync(new(area.Id))).Track;
        await kernel.WaitForTrackIdleAsync(track.Id);
        await kernel.Actors.RemoveAndDisposeAsync(track.Id);

        var engine = new ReportDocEngine();
        var historical = engine.Parse("""
            # Milestones

            ```perga-block checkpoints
            {"items":[{"id":"first","title":"First stage","status":"done"}]}
            ```

            # Progress

            Original prose
            """, docRev: 1);
        await store.SaveReportAsync(track.Id, historical, engine.CompressZstd(historical.Blocks),
            track.LeadSessionId, "Historical import",
            new(null, area.Id, track.Id, "report.updated", JsonSerializer.SerializeToElement(new { docRev = 1 }),
                cluster.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()), default);
        var checkpoint = Assert.Single(historical.Blocks, block => block.Kind == ReportBlockKind.Checkpoints);
        var report = await kernel.ReadReportAsync(track.Id);
        Assert.Contains("First stage", report.RenderedMarkdown);
        var legacyRevision = await store.GetReportRevisionBlocksAsync(track.Id, 1, default);
        Assert.Contains(legacyRevision, block => block.Id == checkpoint.Id && block.Kind == checkpoint.Kind);

        var committed = await kernel.CommitReportAsync(track.Id, track.LeadSessionId!,
            new(report.Anchor, [new BlockOp.ReplaceSectionOp("Progress", "# Progress\n\nNew prose")]));
        var preserved = Assert.Single(committed.Blocks, block => block.Id == checkpoint.Id);
        Assert.Equal(checkpoint.Rev, preserved.Rev);
        Assert.True(JsonElement.DeepEquals(checkpoint.Payload!.Value, preserved.Payload!.Value));
        Assert.Contains("New prose", committed.RenderedMarkdown);

        var denied = await Assert.ThrowsAsync<KernelException>(() => kernel.CommitReportAsync(track.Id, track.LeadSessionId!,
            new(committed.Anchor, [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Checkpoints, null, checkpoint.Payload)])));
        Assert.Equal(KernelErrorCode.InvalidArgument, denied.Code);
        Assert.Equal(committed.DocRev, (await kernel.ReadReportAsync(track.Id)).DocRev);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("checkpoints")]
    public async Task SectionReplacementPreservesUnchangedHistoricalBlocks(string kind)
    {
        await using var cluster = new PergaHostTestCluster();
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        var store = cluster.Services.GetRequiredService<ITrackStore>();
        var area = await kernel.CreateAreaAsync(new("Historical Report", DefaultCwd: cluster.DataDir));
        var track = (await kernel.CreateTrackAsync(new(area.Id))).Track;
        var lead = track.LeadSessionId!;
        await kernel.WaitForTrackIdleAsync(track.Id);
        await kernel.Actors.RemoveAndDisposeAsync(track.Id);

        var engine = new ReportDocEngine();
        var history = engine.Parse($$"""
            # Progress

            Original prose.

            <!-- perga:legacy rev=7 -->
            ```perga-block {{kind}}
            {"historical":true}
            ```
            """, docRev: 1);
        await store.SaveReportAsync(track.Id, history, engine.CompressZstd(history.Blocks),
            lead, "Historical import", new(null, area.Id, track.Id, "report.updated",
                JsonSerializer.SerializeToElement(new { docRev = 1 }), cluster.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()), default);

        var before = await kernel.ReadReportAsync(track.Id, lead);
        var updatedMarkdown = before.RenderedMarkdown.Replace("Original prose.", "New prose.", StringComparison.Ordinal);
        var after = await kernel.CommitReportAsync(track.Id, lead,
            new(before.Anchor, [new BlockOp.ReplaceSectionOp("Progress", updatedMarkdown)]));
        Assert.Equal(updatedMarkdown, after.RenderedMarkdown);
        Assert.Equal(7, Assert.Single(after.Blocks, block => block.Id == "legacy").Rev);
        Assert.DoesNotContain(after.Changes!, change => change.Id == "legacy");
        var storedBlocks = await store.GetReportRevisionBlocksAsync(track.Id, after.DocRev, default);
        Assert.Equal(7, Assert.Single(storedBlocks, block => block.Id == "legacy").Rev);

        var modifiedFence = after.RenderedMarkdown.Replace("\"historical\":true", "\"historical\":false", StringComparison.Ordinal);
        var failure = await Assert.ThrowsAsync<KernelException>(() => kernel.CommitReportAsync(track.Id, lead,
            new(after.Anchor, [new BlockOp.ReplaceSectionOp("Progress", modifiedFence)])));
        Assert.Equal(KernelErrorCode.InvalidArgument, failure.Code);
        var persisted = await kernel.ReadReportAsync(track.Id, lead);
        Assert.Equal(after.DocRev, persisted.DocRev);
        Assert.Equal(after.RenderedMarkdown, persisted.RenderedMarkdown);
    }

    [Fact]
    public async Task McpKernel_ReportSchemas_CompactCommit_And_ScopedRead_WorkOverHttp()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.AuthenticatedClient;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var wsDir = Path.Combine(cluster.DataDir, "mcp-ergonomics-ws");
        Directory.CreateDirectory(wsDir);

        using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "McpErgonomics", defaultCwd = wsDir }, ct);
        areaResp.EnsureSuccessStatusCode();
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, ct))!;

        using var trackResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new { areaId = area.Id, title = "Report Ergonomics", cwd = wsDir },
            ct);
        trackResp.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync(ct));
        var trackId = created.RootElement.GetProperty("track").GetProperty("id").GetString()!;
        var leadSessionId = created.RootElement.GetProperty("track").GetProperty("leadSessionId").GetString()!;
        await cluster.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId, ct);

        var leadToken = (await cluster.Services.GetRequiredService<IPergaKernel>().OpenMcpCallerAsync(leadSessionId, ct)).Token;

        // 1. Verify tools/list publishes enriched nested schemas over Streamable HTTP
        using var listResp = await cluster.PostMcpAsync(
            "/mcp/kernel",
            leadToken,
            new { jsonrpc = "2.0", id = 1, method = "tools/list" },
            ct: ct);
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync(ct));
        var toolsByName = listDoc.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, StringComparer.Ordinal);

        Assert.Equal(
            4,
            toolsByName[KernelMcpToolNames.ReportCommit]
                .GetProperty("inputSchema").GetProperty("properties")
                .GetProperty("ops").GetProperty("items").GetProperty("oneOf")
                .GetArrayLength());
        Assert.True(
            toolsByName[KernelMcpToolNames.ReportRead]
                .GetProperty("inputSchema").GetProperty("properties")
                .TryGetProperty("includeContract", out _));

        // 2. Read initial report, then commit two sections and verify compact commit receipt by default
        var (read0Text, read0Err) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
            cluster, "/mcp/kernel", leadToken, KernelMcpToolNames.ReportRead, new { }, ct);
        Assert.False(read0Err, read0Text);
        using var read0Doc = JsonDocument.Parse(read0Text);

        var (commitText, commitErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.ReportCommit,
            new
            {
                anchor = read0Doc.RootElement.GetProperty("anchor"),
                summary = "Two sections",
                ops = new object[]
                {
                    new { op = "replace_section", sectionH1 = "Overview", markdown = "Overview text." },
                    new { op = "replace_section", sectionH1 = "Notes", markdown = "Notes text." }
                }
            },
            ct);
        Assert.False(commitErr, commitText);
        using var commitDoc = JsonDocument.Parse(commitText);
        Assert.Equal(1, commitDoc.RootElement.GetProperty("docRev").GetInt32());
        Assert.True(commitDoc.RootElement.GetProperty("changes").GetArrayLength() >= 2);
        Assert.False(commitDoc.RootElement.TryGetProperty("renderedMarkdown", out _));
        Assert.False(commitDoc.RootElement.TryGetProperty("blocks", out _));

        // 3. Scoped read with outline + includeContract over MCP
        var (outlineText, outlineErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.ReportRead,
            new { format = "outline", includeContract = true },
            ct);
        Assert.False(outlineErr, outlineText);
        using var outlineDoc = JsonDocument.Parse(outlineText);
        Assert.Equal(2, outlineDoc.RootElement.GetProperty("sections").GetArrayLength());
        Assert.False(outlineDoc.RootElement.TryGetProperty("renderedMarkdown", out _));
        var widgetContract = outlineDoc.RootElement.GetProperty("blockContract").GetProperty("kinds").GetProperty("widget");
        Assert.Equal(new[] { "use", "view" }, widgetContract.GetProperty("required").EnumerateArray().Select(key => key.GetString()));
        var flowExample = widgetContract.GetProperty("flowExample");
        Assert.Equal("builtin:flow", flowExample.GetProperty("use").GetString());
        Assert.Equal("flow", flowExample.GetProperty("view").GetString());

        // 4. Verify HTTP GET and PUT /api/v1/tracks/{trackId}/report* remain unchanged (no 'changes' field)
        var httpReport = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/report", ct);
        Assert.Equal(1, httpReport.GetProperty("docRev").GetInt32());
        Assert.Contains("Notes text.", httpReport.GetProperty("renderedMarkdown").GetString());
        Assert.False(httpReport.TryGetProperty("changes", out _));
    }
}
