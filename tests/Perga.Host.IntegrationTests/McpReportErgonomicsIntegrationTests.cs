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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrackSetReturnsUsableReportAnchorOnlyForFreshOrEmptyReports(bool seedReport)
    {
        await using var cluster = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (trackId, leadId) = await cluster.CreateTrackAsync();
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        var token = (await kernel.OpenMcpCallerAsync(leadId, deadline.Token)).Token;
        const string source = """
            export default { id: 'seed-outline', title: 'Seed outline',
              report: { initialMarkdown: '# Overview\n\nStarter content.' } };
            """;

        var preview = await Call(KernelMcpToolNames.TrackSet, new { source, dryRun = true });
        Assert.False(preview.TryGetProperty("reportAnchor", out _));
        Assert.False(preview.TryGetProperty("reportSections", out _));

        var bound = await Call(KernelMcpToolNames.TrackSet, new { source, seedReport });
        var anchor = bound.GetProperty("reportAnchor");
        Assert.Equal(seedReport ? 1 : 0, anchor.GetProperty("docRev").GetInt32());
        Assert.True(anchor.GetProperty("wholeDocumentRead").GetBoolean());
        if (seedReport)
        {
            Assert.Equal("Overview", Assert.Single(bound.GetProperty("reportSections").EnumerateArray()).GetString());
            Assert.Equal(1, Assert.Single(anchor.GetProperty("knownBlockRevs").EnumerateObject()).Value.GetInt32());
        }
        else
        {
            Assert.False(bound.TryGetProperty("reportSections", out _));
            Assert.Empty(anchor.GetProperty("knownBlockRevs").EnumerateObject());
        }

        var committed = await Call(KernelMcpToolNames.ReportCommit, new
        {
            anchor,
            ops = new[] { new { op = "replace_section", sectionH1 = "Overview", markdown = "Lead's findings." } }
        });
        Assert.Equal(seedReport ? 2 : 1, committed.GetProperty("docRev").GetInt32());

        var rebound = await Call(KernelMcpToolNames.TrackSet, new
        {
            source = """export default { id: 'replacement', title: 'Replacement' };"""
        });
        Assert.False(rebound.TryGetProperty("reportAnchor", out _));
        Assert.False(rebound.TryGetProperty("reportSections", out _));
        Assert.Contains("Lead's findings.", (await kernel.ReadReportAsync(trackId, ct: deadline.Token)).RenderedMarkdown);

        async Task<JsonElement> Call(string tool, object args)
        {
            var (result, error) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster, "/mcp/kernel", token, tool, args, deadline.Token);
            Assert.False(error, result);
            using var document = JsonDocument.Parse(result);
            return document.RootElement.Clone();
        }
    }

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
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        await kernel.Actors.RemoveAndDisposeAsync(trackId);
        var store = cluster.Services.GetRequiredService<ITrackStore>();
        var now = cluster.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var active = new TaskDto("active-task", trackId, "active", "active", 1, "agent", null,
            AccessMode.ReadOnly, false, [], "Active work", null, null, null, TaskRunStatus.Pending, null,
            SemanticVerdict.None, null, now, now);
        await store.UpsertTaskAsync(active, null, ct);
        await store.UpsertTaskAsync(active with
        {
            Id = "archived-task", BlockId = "archived", TaskKey = "archived",
            Status = TaskRunStatus.Done, ArchivedAtMs = now
        }, null, ct);

        var leadToken = (await kernel.OpenMcpCallerAsync(leadSessionId, ct)).Token;

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

        var commitTool = toolsByName[KernelMcpToolNames.ReportCommit];
        var commitSchema = commitTool.GetProperty("inputSchema");
        Assert.Equal(["anchor", "ops"], commitSchema.GetProperty("required").EnumerateArray().Select(key => key.GetString()));
        var commitProperties = commitSchema.GetProperty("properties");
        Assert.Equal(5, commitProperties.GetProperty("ops").GetProperty("items").GetProperty("oneOf").GetArrayLength());
        Assert.Contains("active tasks", commitProperties.GetProperty("includeDocument").GetProperty("description").GetString());
        Assert.Equal(["docRev", "knownBlockRevs", "wholeDocumentRead"],
            commitProperties.GetProperty("anchor").GetProperty("required").EnumerateArray().Select(key => key.GetString()));
        var commitDescription = commitTool.GetProperty("description").GetString();
        Assert.Contains("Cheatsheet:", commitDescription, StringComparison.Ordinal);
        foreach (var op in new[] { "replace_section", "edit_block", "upsert", "delete", "move" })
            Assert.Contains($"op: \"{op}\"", commitDescription, StringComparison.Ordinal);
        Assert.True(
            toolsByName[KernelMcpToolNames.ReportRead]
                .GetProperty("inputSchema").GetProperty("properties")
                .TryGetProperty("includeContract", out _));

        // 2. Commit two sections directly with the initial anchor and verify the compact receipt
        var anchor = new { docRev = 0, knownBlockRevs = new Dictionary<string, int>(), wholeDocumentRead = true };

        var (commitText, commitErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.ReportCommit,
            new
            {
                anchor,
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
        Assert.Equal("active-task", Assert.Single(commitDoc.RootElement.GetProperty("tasks").EnumerateArray()).GetProperty("id").GetString());

        var (staleText, staleErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
            cluster, "/mcp/kernel", leadToken, KernelMcpToolNames.ReportCommit,
            new { anchor, ops = new[] { new { op = "replace_section", sectionH1 = "Notes", markdown = "Stale edit." } } }, ct);
        Assert.True(staleErr, staleText);
        using var staleDoc = JsonDocument.Parse(staleText);
        Assert.Equal("stale_doc_rev", staleDoc.RootElement.GetProperty("code").GetString());

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
        Assert.Equal("active-task", Assert.Single(outlineDoc.RootElement.GetProperty("tasks").EnumerateArray()).GetProperty("id").GetString());
        var widgetContract = outlineDoc.RootElement.GetProperty("blockContract").GetProperty("kinds").GetProperty("widget");
        Assert.Equal(new[] { "use", "view" }, widgetContract.GetProperty("required").EnumerateArray().Select(key => key.GetString()));
        var flowExample = widgetContract.GetProperty("flowExample");
        Assert.Equal("builtin:flow", flowExample.GetProperty("use").GetString());
        Assert.Equal("flow", flowExample.GetProperty("view").GetString());

        var (tasksText, tasksErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
            cluster, "/mcp/kernel", leadToken, KernelMcpToolNames.TaskList, new { }, ct);
        Assert.False(tasksErr, tasksText);
        using var tasksDoc = JsonDocument.Parse(tasksText);
        Assert.Equal(2, tasksDoc.RootElement.GetArrayLength());
        var archived = Assert.Single(tasksDoc.RootElement.EnumerateArray(), task => task.GetProperty("id").GetString() == "archived-task");
        Assert.Equal(now, archived.GetProperty("archivedAtMs").GetInt64());

        var httpReport = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/report", ct);
        Assert.Equal(1, httpReport.GetProperty("docRev").GetInt32());
        Assert.Contains("Notes text.", httpReport.GetProperty("renderedMarkdown").GetString());
        Assert.False(httpReport.TryGetProperty("changes", out _));
        Assert.Equal(2, httpReport.GetProperty("tasks").GetArrayLength());
        Assert.Contains(httpReport.GetProperty("tasks").EnumerateArray(), task => task.GetProperty("id").GetString() == "archived-task");
    }

    [Fact]
    public async Task McpCommitPersistsTargetedEditsSectionPlacementAndContentPreservingUpserts()
    {
        await using var cluster = new PergaHostTestCluster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        var (trackId, leadId) = await cluster.CreateTrackAsync();
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        var token = (await kernel.OpenMcpCallerAsync(leadId, ct)).Token;
        var widgetPayload = new { use = "builtin:flow", view = "flow" };

        var initialAnchor = JsonSerializer.SerializeToElement(new { docRev = 0, knownBlockRevs = new { }, wholeDocumentRead = true });
        var initial = await Commit(initialAnchor,
        [
            new { op = "upsert", id = "notes", sectionH1 = "Progress", markdown = "Draft: typo. Keep this." },
            new { op = "upsert", id = "widget", sectionH1 = "Progress", blockKind = "widget", payload = widgetPayload },
            new { op = "upsert", id = "tail", sectionH1 = "Tail", markdown = "Tail body." }
        ]);
        var originalAnchor = initial.GetProperty("anchor");
        (object Op, string Message)[] invalidEdits =
        [
            (new { op = "replace_section", sectionH1 = "Progress", newSectionH1 = "First\nSecond" },
                "'newSectionH1' must be a single line."),
            (new { op = "edit_block", id = "notes", target = "Missing text", replacement = "" },
                "Target text was not found in block 'notes'.")
        ];
        foreach (var (op, message) in invalidEdits)
        {
            var (errorText, isValidationError) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster, "/mcp/kernel", token, KernelMcpToolNames.ReportCommit,
                new { anchor = originalAnchor, ops = new[] { op } }, ct);
            Assert.True(isValidationError, errorText);
            using var errorDoc = JsonDocument.Parse(errorText);
            Assert.Equal("invalid_argument", errorDoc.RootElement.GetProperty("code").GetString());
            Assert.Equal(400, errorDoc.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(message, errorDoc.RootElement.GetProperty("error").GetString());
        }

        var updated = await Commit(originalAnchor,
        [
            new { op = "edit_block", id = "notes", target = "typo", replacement = "ready" },
            new { op = "edit_block", id = "notes", target = "Draft: ", replacement = "" },
            new { op = "replace_section", sectionH1 = "Progress", newSectionH1 = "Results", afterBlockId = "tail" },
            new { op = "replace_section", sectionH1 = "Insert", markdown = "Inserted body.", beforeBlockId = "tail" }
        ]);

        var blocks = updated.GetProperty("blocks").EnumerateArray().ToArray();
        Assert.Equal(["Insert", "Tail", "Results", "Results"], blocks.Select(block => block.GetProperty("sectionH1").GetString()));
        Assert.Equal("ready. Keep this.", blocks[2].GetProperty("markdown").GetString());
        Assert.Equal(4, blocks[2].GetProperty("rev").GetInt32());
        Assert.Equal(2, blocks[3].GetProperty("rev").GetInt32());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(widgetPayload), blocks[3].GetProperty("payload")));

        var (conflict, isError) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
            cluster, "/mcp/kernel", token, KernelMcpToolNames.ReportCommit, new
            {
                anchor = originalAnchor,
                ops = new[] { new { op = "edit_block", id = "notes", target = "ready", replacement = "stale" } }
            }, ct);
        Assert.True(isError, conflict);
        using var conflictDoc = JsonDocument.Parse(conflict);
        Assert.Equal("stale_block_rev", conflictDoc.RootElement.GetProperty("code").GetString());

        var moved = await Commit(updated.GetProperty("anchor"),
        [
            new { op = "upsert", id = "notes", sectionH1 = "Summary" },
            new { op = "upsert", id = "widget", sectionH1 = "Flow", position = 0 }
        ]);
        Assert.Equal(3, moved.GetProperty("docRev").GetInt32());
        var persisted = await cluster.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/report", ct);
        Assert.Equal(moved.GetProperty("renderedMarkdown").GetString(), persisted.GetProperty("renderedMarkdown").GetString());
        var persistedBlocks = persisted.GetProperty("blocks");
        var widget = persistedBlocks[0];
        Assert.Equal("widget", widget.GetProperty("id").GetString());
        Assert.Equal("Flow", widget.GetProperty("sectionH1").GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(widgetPayload), widget.GetProperty("payload")));
        var notes = Assert.Single(persistedBlocks.EnumerateArray(), block => block.GetProperty("id").GetString() == "notes");
        Assert.Equal("Summary", notes.GetProperty("sectionH1").GetString());
        Assert.Equal("ready. Keep this.", notes.GetProperty("markdown").GetString());

        async Task<JsonElement> Commit(JsonElement anchor, object[] ops)
        {
            var (result, error) = await McpKernelAndVcsIntegrationTests.CallToolAsync(cluster, "/mcp/kernel",
                token, KernelMcpToolNames.ReportCommit, new { anchor, includeDocument = true, ops }, ct);
            Assert.False(error, result);
            using var document = JsonDocument.Parse(result);
            return document.RootElement.Clone();
        }
    }
}
