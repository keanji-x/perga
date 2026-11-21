using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class McpReportErgonomicsIntegrationTests
{
    [Fact]
    public async Task McpKernel_ReportSchemas_CompactCommit_And_ScopedRead_WorkOverHttp()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;
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

        var bridge = cluster.Services.GetRequiredService<IMcpSessionBridge>();
        var registration = bridge.RegisterSession(trackId, leadSessionId, isLead: true, boundTaskId: null, taskRunCount: null, enabledPlugins: []);
        var leadToken = registration.Principal.Token;

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
        Assert.True(outlineDoc.RootElement.TryGetProperty("blockContract", out _));

        // 4. Verify HTTP GET and PUT /api/v1/tracks/{trackId}/report* remain unchanged (no 'changes' field)
        var httpReport = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/report", ct);
        Assert.Equal(1, httpReport.GetProperty("docRev").GetInt32());
        Assert.Contains("Notes text.", httpReport.GetProperty("renderedMarkdown").GetString());
        Assert.False(httpReport.TryGetProperty("changes", out _));

        var firstBlock = httpReport.GetProperty("blocks")[0];
        var firstBlockId = firstBlock.GetProperty("id").GetString()!;
        var firstBlockRev = firstBlock.GetProperty("rev").GetInt32();
        using var putResp = await client.PutAsJsonAsync(
            $"/api/v1/tracks/{trackId}/report/blocks/{firstBlockId}",
            new { docRev = 1, knownBlockRev = firstBlockRev, markdown = "Human edited." },
            ct);
        putResp.EnsureSuccessStatusCode();
        using var putDoc = JsonDocument.Parse(await putResp.Content.ReadAsStringAsync(ct));
        Assert.Equal(2, putDoc.RootElement.GetProperty("docRev").GetInt32());
        Assert.False(putDoc.RootElement.TryGetProperty("changes", out _));
    }
}
