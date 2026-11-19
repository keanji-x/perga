using System.Text.Json;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

public sealed class KernelMcpReportTests
{
    private static McpSessionPrincipal Caller(TrackAggregateSnapshot snap) =>
        new("token", snap.Track.Id, snap.Track.LeadSessionId!, true, null, null, []);

    private static Task<JsonElement> Call(KernelTestContext ctx, McpSessionPrincipal caller, string tool, object args) =>
        KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, tool, TestJson.FromObject(args));

    [Fact]
    public void ToolSchemas_ExplicitlyDescribeNestedContracts()
    {
        var tools = KernelMcpCatalog.GetAllTools().ToDictionary(t => t.Name, StringComparer.Ordinal);

        var commitProps = tools[KernelMcpToolNames.ReportCommit].InputSchema.GetProperty("properties");
        var anchorProps = commitProps.GetProperty("anchor").GetProperty("properties");
        Assert.True(anchorProps.TryGetProperty("docRev", out _));
        Assert.True(anchorProps.TryGetProperty("knownBlockRevs", out _));
        Assert.True(anchorProps.TryGetProperty("wholeDocumentRead", out _));
        Assert.Equal(4, commitProps.GetProperty("ops").GetProperty("items").GetProperty("oneOf").GetArrayLength());
        Assert.Equal("boolean", commitProps.GetProperty("includeDocument").GetProperty("type").GetString());

        var readProps = tools[KernelMcpToolNames.ReportRead].InputSchema.GetProperty("properties");
        var readFormats = readProps.GetProperty("format").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["full", "blocks", "markdown", "outline"], readFormats);
        Assert.True(readProps.TryGetProperty("sectionH1", out _));
        Assert.True(readProps.TryGetProperty("blockIds", out _));
        Assert.True(readProps.TryGetProperty("includeContract", out _));

        var askItemProps = tools[KernelMcpToolNames.UserAsk].InputSchema
            .GetProperty("properties").GetProperty("questions")
            .GetProperty("items").GetProperty("properties");
        Assert.True(askItemProps.TryGetProperty("question", out _));
        Assert.True(askItemProps.TryGetProperty("options", out _));
        Assert.True(askItemProps.TryGetProperty("isMultiSelect", out _));

        var waitModes = tools[KernelMcpToolNames.TerminalRead].InputSchema
            .GetProperty("properties").GetProperty("waitFor").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["none", "elapsed", "change", "text"], waitModes);

        var inputKinds = tools[KernelMcpToolNames.TerminalInput].InputSchema
            .GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["text", "keys", "paste", "submit"], inputKinds);
    }

    [Fact]
    public async Task ReportCommit_ReturnsCompactReceiptByDefault_AndFullDocumentWhenRequested()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snap);

        var initial = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { });
        var anchor0 = initial.GetProperty("anchor");

        var compact = await Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
        {
            anchor = anchor0,
            summary = "Compact test",
            ops = new object[]
            {
                new { op = "replace_section", sectionH1 = "Overview", markdown = "Updated overview prose." }
            }
        });

        Assert.Equal(1, compact.GetProperty("docRev").GetInt32());
        Assert.Equal("Compact test", compact.GetProperty("summary").GetString());
        Assert.True(compact.TryGetProperty("anchor", out var anchor1));
        Assert.True(compact.TryGetProperty("changes", out var changes));
        Assert.True(changes.GetArrayLength() >= 1);
        Assert.True(compact.TryGetProperty("tasks", out _));
        Assert.True(compact.TryGetProperty("diagnostics", out _));
        Assert.False(compact.TryGetProperty("renderedMarkdown", out _));
        Assert.False(compact.TryGetProperty("blocks", out _));

        var withDoc = await Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
        {
            anchor = anchor1,
            includeDocument = true,
            ops = new object[]
            {
                new { op = "replace_section", sectionH1 = "Details", markdown = "Detailed section." }
            }
        });

        Assert.Equal(2, withDoc.GetProperty("docRev").GetInt32());
        Assert.Contains("Detailed section.", withDoc.GetProperty("renderedMarkdown").GetString());
        Assert.True(withDoc.GetProperty("blocks").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task ReportRead_SupportsFormats_ScopedAnchors_AndOnDemandContract()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snap);

        var r0 = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { });
        var c1 = await Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
        {
            anchor = r0.GetProperty("anchor"),
            ops = new object[]
            {
                new { op = "replace_section", sectionH1 = "Overview", markdown = "Overview body." },
                new { op = "replace_section", sectionH1 = "Metrics", markdown = "Metrics body." }
            }
        });
        Assert.Equal(1, c1.GetProperty("docRev").GetInt32());

        // format=blocks omits renderedMarkdown
        var blocksOnly = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { format = "blocks" });
        Assert.False(blocksOnly.TryGetProperty("renderedMarkdown", out _));
        Assert.True(blocksOnly.GetProperty("blocks").GetArrayLength() >= 2);

        // format=markdown omits blocks
        var markdownOnly = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { format = "markdown" });
        Assert.True(markdownOnly.TryGetProperty("renderedMarkdown", out _));
        Assert.False(markdownOnly.TryGetProperty("blocks", out _));

        // format=outline returns sections and outlineBlocks without full prose, and a non-authorizing anchor
        var outline = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { format = "outline" });
        Assert.False(outline.TryGetProperty("renderedMarkdown", out _));
        Assert.False(outline.TryGetProperty("blocks", out _));
        var sections = outline.GetProperty("sections").EnumerateArray().Select(s => s.GetString()!).ToArray();
        Assert.Equal(["Overview", "Metrics"], sections);
        Assert.Equal(2, outline.GetProperty("outlineBlocks").GetArrayLength());
        Assert.False(outline.GetProperty("anchor").GetProperty("wholeDocumentRead").GetBoolean());
        Assert.Empty(outline.GetProperty("anchor").GetProperty("knownBlockRevs").EnumerateObject());

        // sectionH1 scoping returns a partial ReadAnchor that allows editing the read section but rejects blind edits elsewhere
        var scoped = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new
        {
            sectionH1 = "Metrics",
            format = "blocks",
            includeContract = true
        });
        var scopedBlocks = scoped.GetProperty("blocks");
        var metricsBlock = Assert.Single(scopedBlocks.EnumerateArray());
        var metricsBlockId = metricsBlock.GetProperty("id").GetString()!;
        Assert.Equal("Metrics", metricsBlock.GetProperty("sectionH1").GetString());

        var scopedAnchor = scoped.GetProperty("anchor");
        Assert.False(scopedAnchor.GetProperty("wholeDocumentRead").GetBoolean());
        Assert.Single(scopedAnchor.GetProperty("knownBlockRevs").EnumerateObject());

        var contract = scoped.GetProperty("blockContract");
        Assert.True(contract.GetProperty("kinds").TryGetProperty("task", out _));
        Assert.True(contract.GetProperty("kinds").TryGetProperty("table", out _));
        Assert.True(contract.GetProperty("kinds").TryGetProperty("view", out _));
        Assert.Equal("path", Assert.Single(contract.GetProperty("kinds").GetProperty("html").GetProperty("required").EnumerateArray()).GetString());

        // format=markdown + sectionH1 with '# ' prefix renders only the scoped section
        var scopedMd = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new
        {
            sectionH1 = "# Metrics",
            format = "markdown"
        });
        Assert.Contains("Metrics body.", scopedMd.GetProperty("renderedMarkdown").GetString());
        Assert.DoesNotContain("Overview body.", scopedMd.GetProperty("renderedMarkdown").GetString());

        // Blind edit on unread section must be rejected by ReportConflictException
        await Assert.ThrowsAsync<ReportConflictException>(() => Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
        {
            anchor = scopedAnchor,
            ops = new object[]
            {
                new { op = "replace_section", sectionH1 = "Overview", markdown = "Blind overwrite" }
            }
        }));

        // Scoped edit on the read block succeeds
        var scopedCommit = await Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
        {
            anchor = scopedAnchor,
            ops = new object[]
            {
                new { op = "upsert", id = metricsBlockId, blockKind = "prose", markdown = "Metrics updated safely." }
            }
        });
        Assert.Equal(2, scopedCommit.GetProperty("docRev").GetInt32());

        // blockIds filtering returns only the requested block
        var byIds = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new
        {
            blockIds = new[] { metricsBlockId },
            format = "blocks"
        });
        var singleById = Assert.Single(byIds.GetProperty("blocks").EnumerateArray());
        Assert.Equal(metricsBlockId, singleById.GetProperty("id").GetString());
        Assert.Equal("Metrics updated safely.", singleById.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task ReportTools_ReturnActionableErrorsForInvalidArguments()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snap);

        var badFormat = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, caller, KernelMcpToolNames.ReportRead, new { format = "xml" }));
        Assert.Equal(KernelErrorCode.InvalidArgument, badFormat.Code);
        Assert.Contains("outline", badFormat.Message, StringComparison.Ordinal);

        var emptyBlockIds = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, caller, KernelMcpToolNames.ReportRead, new { blockIds = Array.Empty<string>() }));
        Assert.Equal(KernelErrorCode.InvalidArgument, emptyBlockIds.Code);

        var r0 = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { });
        var badIncludeDoc = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
            {
                anchor = r0.GetProperty("anchor"),
                includeDocument = "true",
                ops = new object[] { new { op = "replace_section", sectionH1 = "Overview", markdown = "Should not persist." } }
            }));
        Assert.Equal(KernelErrorCode.InvalidArgument, badIncludeDoc.Code);
        var afterFailedCommit = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { });
        Assert.Equal(0, afterFailedCommit.GetProperty("docRev").GetInt32());

        var badOp = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
            {
                anchor = new { docRev = 0, knownBlockRevs = new { }, wholeDocumentRead = true },
                ops = new object[] { new { op = "bogus_op" } }
            }));
        Assert.Equal(KernelErrorCode.InvalidArgument, badOp.Code);
        Assert.Contains("replace_section", badOp.Message, StringComparison.Ordinal);
        Assert.Contains("upsert", badOp.Message, StringComparison.Ordinal);
    }
}
