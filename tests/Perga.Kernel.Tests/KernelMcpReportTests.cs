using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

public sealed class KernelMcpReportTests
{
    private static Task<JsonElement> Call(KernelTestContext ctx, McpSessionPrincipal caller, string tool, object args) =>
        KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, tool, TestJson.FromObject(args));

    private static McpSessionPrincipal ReadCaller(TrackAggregateSnapshot snapshot) =>
        new("token", snapshot.Track.Id, snapshot.Track.LeadSessionId!, true, null, null, []);

    [Fact]
    public void ToolSchemas_ExplicitlyDescribeNestedContracts()
    {
        var tools = KernelMcpCatalog.GetAllTools().ToDictionary(t => t.Name, StringComparer.Ordinal);

        var commitTool = tools[KernelMcpToolNames.ReportCommit];
        Assert.Equal(["anchor", "ops"], commitTool.InputSchema.GetProperty("required").EnumerateArray().Select(key => key.GetString()));
        var commitProps = commitTool.InputSchema.GetProperty("properties");
        var anchorProps = commitProps.GetProperty("anchor").GetProperty("properties");
        Assert.True(anchorProps.TryGetProperty("docRev", out _));
        Assert.True(anchorProps.TryGetProperty("knownBlockRevs", out _));
        Assert.True(anchorProps.TryGetProperty("wholeDocumentRead", out _));
        Assert.Equal(4, commitProps.GetProperty("ops").GetProperty("items").GetProperty("oneOf").GetArrayLength());
        Assert.Equal("boolean", commitProps.GetProperty("includeDocument").GetProperty("type").GetString());

        var readSchema = tools[KernelMcpToolNames.ReportRead].InputSchema;
        Assert.False(readSchema.GetProperty("additionalProperties").GetBoolean());
        var readProps = readSchema.GetProperty("properties");
        Assert.Equal("string", readProps.GetProperty("trackId").GetProperty("type").GetString());
        Assert.Equal(1, readProps.GetProperty("trackId").GetProperty("minLength").GetInt32());
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
        Assert.False(askItemProps.TryGetProperty("isMultiSelect", out _));

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
    public void ReportCommitDescription_DocumentsAnchorAndOperationFields()
    {
        var tool = Assert.Single(KernelMcpCatalog.GetAllTools(), tool => tool.Name == KernelMcpToolNames.ReportCommit);

        Assert.Contains("Cheatsheet:", tool.Description, StringComparison.Ordinal);
        Assert.Contains("anchor: { docRev, knownBlockRevs, wholeDocumentRead }", tool.Description, StringComparison.Ordinal);
        Assert.Contains("""{ op: "replace_section", sectionH1, markdown }""", tool.Description, StringComparison.Ordinal);
        Assert.Contains("""{ op: "upsert", id?, blockKind?: "prose"|"widget", markdown?, payload?, position? }""", tool.Description, StringComparison.Ordinal);
        Assert.Contains("""{ op: "delete", id }""", tool.Description, StringComparison.Ordinal);
        Assert.Contains("""{ op: "move", id, toIndex }""", tool.Description, StringComparison.Ordinal);
        Assert.Contains("summary, commitMessage, includeDocument", tool.Description, StringComparison.Ordinal);
        Assert.Contains("report_read(includeContract: true)", tool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportCommit_AcceptsInitialAnchorWithoutReadAndRejectsStaleReplacement()
    {
        await using var ctx = new KernelTestContext(reportEngine: new ReportDocEngine());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snap.Track.LeadSessionId!);
        var anchor = TestJson.Parse("""{"docRev":0,"knownBlockRevs":{},"wholeDocumentRead":true}""");

        var commit = await Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
        {
            anchor,
            ops = new[] { new { op = "replace_section", sectionH1 = "Overview", markdown = "Initial plan." } }
        });
        Assert.Equal(1, commit.GetProperty("docRev").GetInt32());

        var conflict = await Assert.ThrowsAsync<ReportConflictException>(() => Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
        {
            anchor,
            ops = new[] { new { op = "replace_section", sectionH1 = "Overview", markdown = "Stale plan." } }
        }));
        Assert.Equal("stale_doc_rev", conflict.Code);
        var report = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { });
        Assert.Equal(1, report.GetProperty("docRev").GetInt32());
        Assert.Contains("Initial plan.", report.GetProperty("renderedMarkdown").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportCommit_ReturnsCompactReceiptByDefault_AndFullDocumentWhenRequested()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snap.Track.LeadSessionId!);

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
        var caller = await ctx.OpenMcpCallerAsync(snap.Track.LeadSessionId!);

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
        Assert.Equal(["prose", "widget"], contract.GetProperty("kinds").EnumerateObject().Select(property => property.Name));
        var widget = contract.GetProperty("kinds").GetProperty("widget");
        var builtins = widget.GetProperty("builtins");
        Assert.Equal(["builtin:flow", "builtin:table", "builtin:list"], builtins.EnumerateObject().Select(property => property.Name));
        var table = builtins.GetProperty("builtin:table").GetProperty("viewResult");
        Assert.Equal("cost", Assert.Single(table.GetProperty("columns").EnumerateArray()).GetProperty("key").GetString());
        Assert.Equal("$24", Assert.Single(table.GetProperty("rows").EnumerateArray()).GetProperty("cost").GetProperty("text").GetString());
        var iframe = widget.GetProperty("iframePostMessage");
        var theme = iframe.GetProperty("hostToWidget")[0].GetProperty("tokens");
        var cssTokens = iframe.GetProperty("cssThemeTokens");
        Assert.All(theme.EnumerateObject(), token => Assert.True(cssTokens.TryGetProperty(token.Name, out _)));
        Assert.Equal("--paper", cssTokens.GetProperty("paper").GetString());


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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportRead_CurrentTrackIdPreservesTheSameResponseAsAnImplicitRead(bool widget)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate("""
            export default defineTemplate({
              id: 'same-track-views', title: 'Same Track views',
              report: { views: { overview: () => ({ items: ['Local data'] }) } }
            });
            """);
        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: widget ? "same-track-views" : null);
        var caller = await ctx.OpenMcpCallerAsync(snap.Track.LeadSessionId!);
        var local = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { });
        const string widgetMarkdown = """
            ```perga-block widget
            {"use":"builtin:list","view":"overview"}
            ```
            """;
        await Call(ctx, caller, KernelMcpToolNames.ReportCommit, new
        {
            anchor = local.GetProperty("anchor"),
            ops = new[]
            {
                new { op = "replace_section", sectionH1 = "Overview", markdown = "Local overview." },
                new { op = "replace_section", sectionH1 = "Details", markdown = widget ? widgetMarkdown : "Local details." }
            }
        });
        var populated = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { });
        Assert.Equal(widget, populated.TryGetProperty("templateViews", out _));
        var blockId = populated.GetProperty("blocks")[0].GetProperty("id").GetString();

        foreach (var options in new object[]
        {
            new { }, new { format = "outline" }, new { format = "blocks", includeContract = true },
            new { sectionH1 = "Details" }, new { blockIds = new[] { blockId } }
        })
        {
            var args = TestJson.FromObject(options).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            var implicitRead = await Call(ctx, caller, KernelMcpToolNames.ReportRead, args);
            args["trackId"] = TestJson.FromObject(caller.TrackId);
            var explicitRead = await Call(ctx, caller, KernelMcpToolNames.ReportRead, args);
            Assert.Equal(implicitRead.GetRawText(), explicitRead.GetRawText());
            Assert.True(explicitRead.TryGetProperty("anchor", out _));
            Assert.True(explicitRead.TryGetProperty("tasks", out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportRead_CrossTrackProjectsAnOpenOrClosedProseSnapshotWithoutActivatingIt(bool closed)
    {
        await using var ctx = new KernelTestContext();
        var (area, source) = await ctx.CreateDefaultTrackAsync();
        var target = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Peer"));
        var targetCaller = await ctx.OpenMcpCallerAsync(target.Track.LeadSessionId!);
        var local = await Call(ctx, targetCaller, KernelMcpToolNames.ReportRead, new { });
        var commit = await Call(ctx, targetCaller, KernelMcpToolNames.ReportCommit, new
        {
            anchor = local.GetProperty("anchor"),
            ops = new[]
            {
                new { op = "replace_section", sectionH1 = "Overview", markdown = "Overview text." },
                new { op = "replace_section", sectionH1 = "Metrics", markdown = "Metrics text." }
            },
            summary = "Peer report",
            includeDocument = true
        });
        var trackId = target.Track.Id;
        var metricsId = commit.GetProperty("blocks")[1].GetProperty("id").GetString();
        if (closed)
            await ctx.Kernel.CloseTrackAsync(trackId, target.Track.LeadSessionId!);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(trackId);
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));

        Task<JsonElement> Read(object arguments) => Call(ctx, ReadCaller(source), KernelMcpToolNames.ReportRead, arguments);

        var full = await Read(new { trackId });
        Assert.Equal(commit.GetProperty("docRev").GetInt32(), full.GetProperty("docRev").GetInt32());
        Assert.Equal("Peer report", full.GetProperty("summary").GetString());
        Assert.Equal(2, full.GetProperty("blocks").GetArrayLength());
        Assert.Contains("Overview text.", full.GetProperty("renderedMarkdown").GetString());

        var scoped = await Read(new { trackId, format = "blocks", sectionH1 = "# Metrics", includeContract = true });
        Assert.Equal(metricsId, Assert.Single(scoped.GetProperty("blocks").EnumerateArray()).GetProperty("id").GetString());
        Assert.True(scoped.GetProperty("blockContract").TryGetProperty("kinds", out _));
        Assert.False(scoped.TryGetProperty("renderedMarkdown", out _));

        var markdown = await Read(new { trackId, format = "markdown", blockIds = new[] { metricsId } });
        Assert.Contains("Metrics text.", markdown.GetProperty("renderedMarkdown").GetString());
        Assert.DoesNotContain("Overview text.", markdown.GetProperty("renderedMarkdown").GetString());
        Assert.False(markdown.TryGetProperty("blocks", out _));

        var outline = await Read(new { trackId, format = "outline" });
        Assert.Equal(["Overview", "Metrics"], outline.GetProperty("sections").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(2, outline.GetProperty("outlineBlocks").GetArrayLength());
        Assert.False(outline.TryGetProperty("renderedMarkdown", out _));
        Assert.False(outline.TryGetProperty("blocks", out _));

        foreach (var response in new[] { full, scoped, markdown, outline })
        {
            Assert.Equal(trackId, response.GetProperty("trackId").GetString());
            Assert.True(response.GetProperty("readOnly").GetBoolean());
            Assert.False(response.TryGetProperty("anchor", out _));
            Assert.False(response.TryGetProperty("tasks", out _));
            Assert.False(response.TryGetProperty("templateViews", out _));
        }
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));
    }

    [Fact]
    public async Task ReportRead_OnlyLoadsAWidgetTracksTemplateViewsForContentFormats()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (area, caller) = await ctx.CreateDefaultTrackAsync();
        ctx.Kernel.Templates.RegisterTemplate("""
            export default defineTemplate({
              id: 'cross-views', title: 'Cross-track views',
              report: {
                views: {
                  privateTasks: (_, kernel) => {
                    kernel.unreferencedViewRan = true;
                    return kernel.tasks;
                  },
                  overview: (_, kernel) => ({
                    items: [kernel.unreferencedViewRan ? 'Unreferenced view ran' : 'Visible from another Track']
                  }),
                  flow: (_, kernel) => kernel.tasks
                }
              }
            });
            """);
        var target = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, TemplateId: "cross-views"));
        var trackId = target.Track.Id;
        var targetCaller = await ctx.OpenMcpCallerAsync(target.Track.LeadSessionId!);
        var local = await Call(ctx, targetCaller, KernelMcpToolNames.ReportRead, new { });
        await Call(ctx, targetCaller, KernelMcpToolNames.ReportCommit, new
        {
            anchor = local.GetProperty("anchor"),
            ops = new[]
            {
                new { op = "replace_section", sectionH1 = "Prose", markdown = "Visible text." },
                new { op = "replace_section", sectionH1 = "Widget", markdown = """
                    ```perga-block widget
                    {"use":"builtin:list","view":"overview"}
                    ```
                    """ },
                new { op = "replace_section", sectionH1 = "Flow", markdown = """
                    ```perga-block widget
                    {"use":"builtin:flow","view":"flow"}
                    ```
                    """ }
            }
        });
        await ctx.Kernel.CloseTrackAsync(trackId, target.Track.LeadSessionId!);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(trackId);

        var outline = await Call(ctx, ReadCaller(caller), KernelMcpToolNames.ReportRead, new { trackId, format = "outline" });
        Assert.False(outline.TryGetProperty("templateViews", out _));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));

        var prose = await Call(ctx, ReadCaller(caller), KernelMcpToolNames.ReportRead, new { trackId, sectionH1 = "Prose" });
        Assert.False(prose.TryGetProperty("templateViews", out _));
        var widget = await Call(ctx, ReadCaller(caller), KernelMcpToolNames.ReportRead, new { trackId, sectionH1 = "Widget" });
        var view = Assert.Single(widget.GetProperty("templateViews").EnumerateObject());
        Assert.Equal("overview", view.Name);
        var widgetBlock = Assert.Single(widget.GetProperty("blocks").EnumerateArray(), block => block.GetProperty("kind").GetString() == "widget");
        var byId = await Call(ctx, ReadCaller(caller), KernelMcpToolNames.ReportRead, new
        {
            trackId, blockIds = new[] { widgetBlock.GetProperty("id").GetString() }
        });
        Assert.Equal("overview", Assert.Single(byId.GetProperty("templateViews").EnumerateObject()).Name);
        var flow = await Call(ctx, ReadCaller(caller), KernelMcpToolNames.ReportRead, new { trackId, sectionH1 = "Flow" });
        Assert.False(flow.TryGetProperty("templateViews", out _));

        var full = await Call(ctx, ReadCaller(caller), KernelMcpToolNames.ReportRead, new { trackId });
        Assert.Equal("overview", Assert.Single(full.GetProperty("templateViews").EnumerateObject()).Name);
        Assert.Equal("Visible from another Track", full.GetProperty("templateViews").GetProperty("overview")
            .GetProperty("items")[0].GetString());
        Assert.False(full.TryGetProperty("anchor", out _));
        Assert.False(full.TryGetProperty("tasks", out _));

        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));
    }

    [Fact]
    public async Task ReportRead_MissingTrackReturnsNotFoundWithoutActivatingIt()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        const string trackId = "trk_missing_report";
        var error = await Assert.ThrowsAsync<KernelException>(() =>
            Call(ctx, ReadCaller(snap), KernelMcpToolNames.ReportRead, new { trackId }));
        Assert.Equal(KernelErrorCode.NotFound, error.Code);
        Assert.Equal($"Track '{trackId}' not found.", error.Message);
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalReadOnlyToolsBypassBeforeWhileOpenWorldReadsAndWritesRemainGuarded(bool crashes)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (area, snap) = await ctx.CreateDefaultTrackAsync();
        var peer = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Peer"));
        var caller = await ctx.OpenMcpCallerAsync(snap.Track.LeadSessionId!);
        var terminal = await ctx.Kernel.OpenTerminalAsync(snap.Track.Id, caller.SessionId, new OpenTerminalRequest(Title: "Test"));
        var hookBody = crashes ? "throw new Error('guarded tool');" : "return Deny('guarded tool');";
        await Call(ctx, caller, KernelMcpToolNames.TrackSet, new
        {
            source = $$"""
                export default defineTemplate({
                  id: 'read-policy', title: 'Read policy',
                  before: () => { {{hookBody}} }
                });
                """
        });

        var local = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { });
        Assert.True(local.TryGetProperty("anchor", out _));
        var peerReport = await Call(ctx, caller, KernelMcpToolNames.ReportRead, new { trackId = peer.Track.Id });
        Assert.Equal(peer.Track.Id, peerReport.GetProperty("trackId").GetString());
        foreach (var (tool, args) in new (string, object)[]
        {
            (KernelMcpToolNames.TaskList, new { }),
            (KernelMcpToolNames.TrackList, new { area = "*" }),
            (KernelMcpToolNames.AreaList, new { }),
            (KernelMcpToolNames.MailList, new { }),
            (KernelMcpToolNames.SessionList, new { }),
            (KernelMcpToolNames.SessionRead, new { sessionId = caller.SessionId }),
            (KernelMcpToolNames.TrackStatus, new { }),
            (KernelMcpToolNames.FlowRead, new { }),
            (KernelMcpToolNames.ProviderList, new { }),
            (KernelMcpToolNames.TemplateList, new { }),
            (KernelMcpToolNames.TemplateRead, new { templateId = "__current__" })
        })
            await Call(ctx, caller, tool, args);

        var alternateCase = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.InvokeKernelMcpToolAsync(
            caller, KernelMcpToolNames.ReportRead, TestJson.Parse("""{"TrackId":"trk_protected"}""")));
        Assert.Equal(KernelErrorCode.InvalidArgument, alternateCase.Code);

        foreach (var (tool, args) in new (string, object)[]
        {
            (KernelMcpToolNames.TerminalRead, new { terminalId = terminal.Id }),
            (KernelMcpToolNames.SystemUpgradeCheck, new { }),
            (KernelMcpToolNames.ReportCommit, new
            {
                anchor = local.GetProperty("anchor"),
                ops = new[] { new { op = "upsert", blockKind = "prose", markdown = "# Guarded" } }
            })
        })
        {
            var failure = await Record.ExceptionAsync(() => Call(ctx, caller, tool, args));
            if (crashes)
                Assert.IsType<InvalidOperationException>(failure);
            else
                Assert.Equal(KernelErrorCode.PolicyDenied, Assert.IsType<KernelException>(failure).Code);
            Assert.Contains("guarded tool", failure!.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReportRead_CrossTrackProjectsWidgetStoredInMarkdown()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (area, source) = await ctx.CreateDefaultTrackAsync();
        ctx.Kernel.Templates.RegisterTemplate("""
            export default defineTemplate({
              id: 'markdown-widget', title: 'Markdown widget',
              report: { views: { summary: () => ({ count: 3 }), unused: (_, kernel) => kernel.tasks } }
            });
            """);
        var target = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, TemplateId: "markdown-widget"));
        var targetCaller = await ctx.OpenMcpCallerAsync(target.Track.LeadSessionId!);
        var local = await Call(ctx, targetCaller, KernelMcpToolNames.ReportRead, new { });
        await Call(ctx, targetCaller, KernelMcpToolNames.ReportCommit, new
        {
            anchor = local.GetProperty("anchor"),
            ops = new[] { new { op = "upsert", id = "b_widget", blockKind = "widget", markdown = """{"use":"builtin:list","view":"summary"}""" } }
        });
        await ctx.Kernel.WaitForTrackIdleAsync(target.Track.Id);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(target.Track.Id);

        var response = await Call(ctx, ReadCaller(source), KernelMcpToolNames.ReportRead,
            new { trackId = target.Track.Id, blockIds = new[] { "b_widget" }, format = "blocks" });
        var view = Assert.Single(response.GetProperty("templateViews").EnumerateObject());
        Assert.Equal("summary", view.Name);
        Assert.Equal(3, view.Value.GetProperty("count").GetInt32());
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(target.Track.Id));
    }

    [Theory]
    [InlineData("""{"trackId":" "}""")]
    [InlineData("""{"trackId":5}""")]
    [InlineData("""{"trackId":null}""")]
    [InlineData("""{"TrackId":"trk_other"}""")]
    [InlineData("""{"FORMAT":"outline"}""")]
    [InlineData("""{"track_id":"trk_other"}""")]
    [InlineData("""{"unknown":true}""")]
    [InlineData("""{"unknown":null}""")]
    [InlineData("""{"section":"Overview"}""")]
    [InlineData("""{"section_h1":"Overview"}""")]
    [InlineData("""{"block_ids":["b_1"]}""")]
    [InlineData("""{"include_contract":true}""")]
    [InlineData("[]")]
    public async Task ReportRead_RejectsInvalidOrUnknownArguments(string arguments)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.InvokeKernelMcpToolAsync(
            ReadCaller(snap), KernelMcpToolNames.ReportRead, TestJson.Parse(arguments)));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
    }

    [Theory]
    [InlineData("""{"trackId":true}""")]
    [InlineData("""{"trackId":" "}""")]
    public async Task ReportCommit_RejectsInvalidTrackIds(string arguments)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snap.Track.LeadSessionId!);
        var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.InvokeKernelMcpToolAsync(
            caller, KernelMcpToolNames.ReportCommit, TestJson.Parse(arguments)));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
        Assert.Contains("only supports writing the caller's Track", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportTools_ReturnActionableErrorsForInvalidArguments()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snap.Track.LeadSessionId!);

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
