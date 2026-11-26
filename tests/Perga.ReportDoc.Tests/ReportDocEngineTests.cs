using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perga.ReportDoc.Tests;

public sealed partial class ReportDocEngineTests
{
    private readonly ReportDocEngine _engine = new();

    [GeneratedRegex(@"^b_[0-9a-f]{4}$")]
    private static partial Regex SixCharBlockIdRegex();

    [Fact]
    public void Parse_SplitsH1AndPergaBlocks_PreservesLeadingComment_AndGeneratesStable6CharIds()
    {
        var raw = """
            <!-- track-report: rev-seed -->
            <!-- second-header-comment -->

            Preamble before any H1 heading.

            # Overview

            Overview paragraph 1.

            ```markdown
            # Heading inside normal code fence must NOT split!
            ```

            Overview paragraph 2.

            ```perga-block task
            {"key":"implement-parser","kind":"agent","ready":true}
            ```

            ```perga-block html
            <div class="kpi">Latency: 1.2ms</div>
            ```

            # Metrics

            ```perga-block table
            {"columns":["Suite","Passed"],"rows":[["ReportDoc",24]]}
            ```

            ```perga-block view
            {"component":"dag-graph","zoom":1}
            ```

            # Empty Trailing Section
            """;

        var doc1 = _engine.Parse(raw, docRev: 3, summary: "Initial parse");
        var doc2 = _engine.Parse(raw, docRev: 3, summary: "Initial parse");

        Assert.Equal(3, doc1.DocRev);
        Assert.Equal("Initial parse", doc1.Summary);
        Assert.Equal("<!-- track-report: rev-seed -->\n<!-- second-header-comment -->", doc1.LeadingComment);

        Assert.Equal(7, doc1.Blocks.Count);

        // Deterministic stable 6-char IDs across repeated parses of identical unmarked Markdown
        for (var i = 0; i < doc1.Blocks.Count; i++)
        {
            Assert.Matches(SixCharBlockIdRegex(), doc1.Blocks[i].Id);
            Assert.Equal(doc1.Blocks[i].Id, doc2.Blocks[i].Id);
            Assert.Equal(1, doc1.Blocks[i].Rev);
        }

        Assert.Equal(doc1.Blocks.Count, doc1.Blocks.Select(b => b.Id).Distinct().Count());

        // 0: Preamble prose
        Assert.Equal(ReportBlockKind.Prose, doc1.Blocks[0].Kind);
        Assert.Null(doc1.Blocks[0].SectionH1);
        Assert.Equal("Preamble before any H1 heading.", doc1.Blocks[0].Markdown);

        // 1: Overview prose (including normal fenced code block with inner # line)
        Assert.Equal(ReportBlockKind.Prose, doc1.Blocks[1].Kind);
        Assert.Equal("Overview", doc1.Blocks[1].SectionH1);
        Assert.Contains("# Heading inside normal code fence must NOT split!", doc1.Blocks[1].Markdown);
        Assert.Contains("Overview paragraph 2.", doc1.Blocks[1].Markdown);

        // 2: Task block
        Assert.Equal(ReportBlockKind.Task, doc1.Blocks[2].Kind);
        Assert.Equal("Overview", doc1.Blocks[2].SectionH1);
        Assert.Null(doc1.Blocks[2].Markdown);
        Assert.Equal("implement-parser", doc1.Blocks[2].Payload?.GetProperty("key").GetString());

        // 3: Raw HTML block
        Assert.Equal(ReportBlockKind.Html, doc1.Blocks[3].Kind);
        Assert.Equal("Overview", doc1.Blocks[3].SectionH1);
        Assert.Equal("<div class=\"kpi\">Latency: 1.2ms</div>", doc1.Blocks[3].Markdown);
        Assert.Null(doc1.Blocks[3].Payload);

        // 4: Table block under Metrics
        Assert.Equal(ReportBlockKind.Table, doc1.Blocks[4].Kind);
        Assert.Equal("Metrics", doc1.Blocks[4].SectionH1);
        Assert.NotNull(doc1.Blocks[4].Payload);

        // 5: View block under Metrics
        Assert.Equal(ReportBlockKind.View, doc1.Blocks[5].Kind);
        Assert.Equal("Metrics", doc1.Blocks[5].SectionH1);
        Assert.Equal("dag-graph", doc1.Blocks[5].Payload?.GetProperty("component").GetString());

        // 6: Empty trailing H1 section preserved
        Assert.Equal(ReportBlockKind.Prose, doc1.Blocks[6].Kind);
        Assert.Equal("Empty Trailing Section", doc1.Blocks[6].SectionH1);
        Assert.Equal(string.Empty, doc1.Blocks[6].Markdown);
    }

    [Fact]
    public void RenderMarkdown_RoundTripsWithMarkers_AndRendersCleanlyWithoutMarkers()
    {
        var source = """
            <!-- doc-header: architecture -->

            # Plan

            <!-- perga:b_1a2b -->
            Step one details.

            <!-- perga:b_3c4d -->
            Step two in separate prose block.

            <!-- perga:b_5e6f -->
            ```perga-block task
            {
              "key": "t_verify",
              "goal": "Run verification suite"
            }
            ```

            # Notes

            <!-- perga:b_7a8b -->
            Final verification notes.
            """.Replace("\r\n", "\n");

        var parsed = _engine.Parse(source, docRev: 5, summary: "v5");
        Assert.Equal(["b_1a2b", "b_3c4d", "b_5e6f", "b_7a8b"], parsed.Blocks.Select(b => b.Id).ToArray());

        var renderedWithMarkers = _engine.RenderMarkdown(parsed, withMarkers: true);
        Assert.Equal(source, renderedWithMarkers);

        var reparsed = _engine.Parse(renderedWithMarkers, docRev: 5, summary: "v5");
        Assert.Equal(parsed.LeadingComment, reparsed.LeadingComment);
        Assert.Equal(parsed.Blocks.Count, reparsed.Blocks.Count);
        for (var i = 0; i < parsed.Blocks.Count; i++)
        {
            Assert.Equal(parsed.Blocks[i].Id, reparsed.Blocks[i].Id);
            Assert.Equal(parsed.Blocks[i].Kind, reparsed.Blocks[i].Kind);
            Assert.Equal(parsed.Blocks[i].SectionH1, reparsed.Blocks[i].SectionH1);
            Assert.Equal(parsed.Blocks[i].Markdown, reparsed.Blocks[i].Markdown);
            Assert.Equal(parsed.Blocks[i].Payload?.GetRawText(), reparsed.Blocks[i].Payload?.GetRawText());
        }

        var cleanRendered = _engine.RenderMarkdown(parsed, withMarkers: false);
        Assert.DoesNotContain("<!-- perga:", cleanRendered);
        Assert.Contains("<!-- doc-header: architecture -->", cleanRendered);
        Assert.Contains("# Plan", cleanRendered);
        Assert.Contains("```perga-block task", cleanRendered);
        Assert.Contains("# Notes", cleanRendered);
    }

    [Fact]
    public void Parse_SupportsMarkersPlacedImmediatelyBeforeH1Headings()
    {
        var raw = """
            <!-- perga:b_aa11 -->
            # First Section

            Body of first section.

            <!-- perga:b_bb22 -->
            # Second Section

            Body of second section.
            """;

        var doc = _engine.Parse(raw);
        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal("b_aa11", doc.Blocks[0].Id);
        Assert.Equal("First Section", doc.Blocks[0].SectionH1);
        Assert.Equal("Body of first section.", doc.Blocks[0].Markdown);

        Assert.Equal("b_bb22", doc.Blocks[1].Id);
        Assert.Equal("Second Section", doc.Blocks[1].SectionH1);
        Assert.Equal("Body of second section.", doc.Blocks[1].Markdown);
    }

    [Fact]
    public void ApplyCommit_SupportsUpsertInsertUpdate_Delete_Move_AndReplaceSection()
    {
        var initial = _engine.Parse("""
            # Overview

            <!-- perga:b_0001 -->
            Initial overview.

            # Tasks

            <!-- perga:b_0002 -->
            ```perga-block task
            {"key":"task-1","ready":false}
            ```

            # Scratch

            <!-- perga:b_0003 -->
            Temporary scratch note.
            """, docRev: 1, summary: "s1");

        // 1. Upsert (update b_0002 + insert new task at position 2) + Delete b_0003
        using var updatedTaskPayload = JsonDocument.Parse("""{"key":"task-1","ready":true}""");
        using var insertedTaskPayload = JsonDocument.Parse("""{"key":"task-2","ready":true}""");

        var anchor1 = new ReadAnchor(1, new Dictionary<string, int>(), WholeDocumentRead: true);
        var commit1 = _engine.ApplyCommit(
            initial,
            anchor1,
            [
                new BlockOp.UpsertBlockOp("b_0002", ReportBlockKind.Task, null, updatedTaskPayload.RootElement),
                new BlockOp.UpsertBlockOp(null, ReportBlockKind.Task, null, insertedTaskPayload.RootElement, Position: 2),
                new BlockOp.DeleteBlockOp("b_0003")
            ],
            newSummary: "Updated tasks and removed scratch");

        Assert.Equal(2, commit1.NextDocument.DocRev);
        Assert.Equal("Updated tasks and removed scratch", commit1.NextDocument.Summary);
        Assert.Equal(3, commit1.NextDocument.Blocks.Count);

        var updatedB2 = commit1.NextDocument.Blocks[1];
        Assert.Equal("b_0002", updatedB2.Id);
        Assert.Equal(2, updatedB2.Rev);
        Assert.True(updatedB2.Payload?.GetProperty("ready").GetBoolean());

        var insertedTask = commit1.NextDocument.Blocks[2];
        Assert.Matches(SixCharBlockIdRegex(), insertedTask.Id);
        Assert.Equal(1, insertedTask.Rev);
        Assert.Equal("Tasks", insertedTask.SectionH1);

        Assert.Contains(commit1.Changes, c => c.Id == "b_0002" && c.PreviousRev == 1 && c.CurrentRev == 2 && c.Position == 1 && !c.Moved);
        Assert.Contains(commit1.Changes, c => c.Id == insertedTask.Id && c.PreviousRev is null && c.CurrentRev == 1 && c.Position == 2 && !c.Moved);
        Assert.Contains(commit1.Changes, c => c.Id == "b_0003" && c.PreviousRev == 1 && c.CurrentRev is null && c.Position is null);

        // 2. MoveBlockOp: reorder insertedTask before b_0002 inside # Tasks
        var anchor2 = new ReadAnchor(
            DocRev: 1, // Even with older DocRev, KnownBlockRevs on target block authorizes move!
            KnownBlockRevs: new Dictionary<string, int> { [insertedTask.Id] = 1 },
            WholeDocumentRead: false);

        var commit2 = _engine.ApplyCommit(
            commit1.NextDocument,
            anchor2,
            [new BlockOp.MoveBlockOp(insertedTask.Id, ToIndex: 1)]);

        Assert.Equal(3, commit2.NextDocument.DocRev);
        Assert.Equal(["b_0001", insertedTask.Id, "b_0002"], commit2.NextDocument.Blocks.Select(b => b.Id).ToArray());
        var moveChange = Assert.Single(commit2.Changes);
        Assert.Equal(insertedTask.Id, moveChange.Id);
        Assert.True(moveChange.Moved);
        Assert.Equal(1, moveChange.Position);
        Assert.Equal(insertedTask.Rev, moveChange.CurrentRev);

        // 3. ReplaceSectionOp: replace # Overview with prose + html block, and append new # Conclusion section
        var anchor3 = new ReadAnchor(3, new Dictionary<string, int>(), WholeDocumentRead: true);
        var commit3 = _engine.ApplyCommit(
            commit2.NextDocument,
            anchor3,
            [
                new BlockOp.ReplaceSectionOp(
                    "Overview",
                    """
                    Refined overview summary.

                    ```perga-block html
                    <p>Status: Ready</p>
                    ```
                    """),
                new BlockOp.ReplaceSectionOp("Conclusion", "Shipped cleanly.")
            ]);

        Assert.Equal(4, commit3.NextDocument.DocRev);
        Assert.Equal(5, commit3.NextDocument.Blocks.Count);
        Assert.Equal("Overview", commit3.NextDocument.Blocks[0].SectionH1);
        Assert.Equal("Refined overview summary.", commit3.NextDocument.Blocks[0].Markdown);
        Assert.Equal(ReportBlockKind.Html, commit3.NextDocument.Blocks[1].Kind);
        Assert.Equal("Overview", commit3.NextDocument.Blocks[1].SectionH1);
        Assert.Equal("Tasks", commit3.NextDocument.Blocks[2].SectionH1);
        Assert.Equal("Conclusion", commit3.NextDocument.Blocks[4].SectionH1);
        Assert.Equal("Shipped cleanly.", commit3.NextDocument.Blocks[4].Markdown);
        Assert.Contains(commit3.Changes, c => c.Id == "b_0001" && c.PreviousRev == 1 && c.CurrentRev is null);
    }

    [Fact]
    public void ApplyCommit_EnforcesOptimisticConcurrency_WholeDocumentRead_And_KnownBlockRevs()
    {
        var doc0 = _engine.Parse("""
            # Section A

            <!-- perga:b_aaaa -->
            Alpha v1

            # Section B

            <!-- perga:b_bbbb -->
            Beta v1
            """, docRev: 1);

        // Session 1 edits b_aaaa -> DocRev becomes 2, b_aaaa.Rev = 2, b_bbbb.Rev = 1
        var res1 = _engine.ApplyCommit(
            doc0,
            new ReadAnchor(1, new Dictionary<string, int>(), WholeDocumentRead: true),
            [new BlockOp.UpsertBlockOp("b_aaaa", ReportBlockKind.Prose, "Alpha v2", null)]);

        var doc1 = res1.NextDocument;
        Assert.Equal(2, doc1.DocRev);

        // Conflict 1: WholeDocumentRead=true but stale DocRev=1 without KnownBlockRevs
        var staleWholeEx = Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(
                doc1,
                new ReadAnchor(1, new Dictionary<string, int>(), WholeDocumentRead: true),
                [new BlockOp.UpsertBlockOp("b_bbbb", ReportBlockKind.Prose, "Beta blind write", null)]));
        Assert.Equal("stale_doc_rev", staleWholeEx.Code);
        Assert.Contains("b_bbbb", staleWholeEx.Message);
        Assert.NotEmpty(staleWholeEx.NextStep);

        // Conflict 2: KnownBlockRevs has stale block rev for b_aaaa (rev 1 vs current rev 2)
        var staleBlockEx = Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(
                doc1,
                new ReadAnchor(1, new Dictionary<string, int> { ["b_aaaa"] = 1 }, WholeDocumentRead: false),
                [new BlockOp.UpsertBlockOp("b_aaaa", ReportBlockKind.Prose, "Alpha overwrite", null)]));
        Assert.Equal("stale_block_rev", staleBlockEx.Code);
        Assert.Contains("b_aaaa", staleBlockEx.Message);

        // Conflict 3: Unread block on DeleteBlockOp, MoveBlockOp, and ReplaceSectionOp
        var unreadAnchor = new ReadAnchor(2, new Dictionary<string, int>(), WholeDocumentRead: false);

        Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc1, unreadAnchor, [new BlockOp.DeleteBlockOp("b_bbbb")]));

        Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc1, unreadAnchor, [new BlockOp.MoveBlockOp("b_bbbb", 0)]));

        Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc1, unreadAnchor, [new BlockOp.ReplaceSectionOp("Section B", "Illegal replace")]));

        // Pass: Concurrent non-overlapping edit to b_bbbb succeeds via KnownBlockRevs["b_bbbb"] = 1 even though DocRev advanced to 2!
        var concurrentPass = _engine.ApplyCommit(
            doc1,
            new ReadAnchor(1, new Dictionary<string, int> { ["b_bbbb"] = 1 }, WholeDocumentRead: false),
            [new BlockOp.UpsertBlockOp("b_bbbb", ReportBlockKind.Prose, "Beta v2 concurrent", null)]);

        Assert.Equal(3, concurrentPass.NextDocument.DocRev);
        Assert.Equal(2, concurrentPass.NextDocument.Blocks[1].Rev);
        Assert.Equal("Beta v2 concurrent", concurrentPass.NextDocument.Blocks[1].Markdown);
    }

    [Fact]
    public void EditBlockMatchesExactNormalizedTextAndChainsEditsUsingOneScopedAnchor()
    {
        var doc = _engine.Parse("# Notes\n\n<!-- perga:notes rev=4 -->\nOld\r\nsentence. old sentence. Keep this.", docRev: 2);
        var anchor = new ReadAnchor(2, new Dictionary<string, int> { ["notes"] = 4 }, WholeDocumentRead: false);

        var result = _engine.ApplyCommit(doc, anchor,
        [
            new BlockOp.EditBlockOp("notes", "Old\r\nsentence.", "New\r\nline."),
            new BlockOp.EditBlockOp("notes", "New\rline. ", "")
        ]);

        Assert.Equal("old sentence. Keep this.", Assert.Single(result.NextDocument.Blocks).Markdown);
        Assert.Equal(new BlockChangeRecord("notes", ReportBlockKind.Prose, 4, 6, 0, Moved: false), Assert.Single(result.Changes));
        Assert.Equal("Old\nsentence. old sentence. Keep this.", Assert.Single(doc.Blocks).Markdown);
    }

    [Theory]
    [InlineData("notes", "Banana", "Target text was not found in block 'notes'")]
    [InlineData("notes", "ana", "Target text matched multiple locations in block 'notes'")]
    [InlineData("widget", "banana", "Block 'widget' has kind 'Widget'")]
    public void EditBlockRejectsMissingOrOverlappingMatchesAndNonProse(string id, string target, string message)
    {
        var doc = _engine.Parse("""
            <!-- perga:notes -->
            banana

            <!-- perga:widget -->
            ```perga-block widget
            {"use":"builtin:flow","view":"flow"}
            ```
            """);

        var error = Assert.Throws<ArgumentException>(() =>
            _engine.ApplyCommit(doc, WholeAnchor(doc), [new BlockOp.EditBlockOp(id, target, "")]));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EditBlockUsesTheExistingBlockConflictCodes()
    {
        var doc = _engine.Parse("<!-- perga:notes rev=3 -->\nBody", docRev: 5);
        var edit = new BlockOp.EditBlockOp("notes", "Body", "Updated");
        var stale = new ReadAnchor(4, new Dictionary<string, int> { ["notes"] = 2 }, WholeDocumentRead: false);
        var unread = new ReadAnchor(5, new Dictionary<string, int>(), WholeDocumentRead: false);

        Assert.Equal("stale_block_rev", Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc, stale, [edit])).Code);
        Assert.Equal("unread_block", Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc, unread, [edit])).Code);
        Assert.Equal("block_deleted", Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc with { Blocks = [] }, stale, [edit])).Code);
        Assert.Throws<KeyNotFoundException>(() => _engine.ApplyCommit(doc, WholeAnchor(doc), [edit with { Id = "missing" }]));
    }

    [Fact]
    public void RenameSectionInPlacePreservesAllBlocksAndTheirPositions()
    {
        var doc = _engine.Parse("""
            # Progress

            <!-- perga:notes rev=3 -->
            Notes body.

            # Other

            <!-- perga:other -->
            Other body.

            # Progress

            <!-- perga:widget rev=5 -->
            ```perga-block widget
            {"use":"builtin:flow","view":"flow"}
            ```
            """, docRev: 8);

        var partial = new ReadAnchor(8, new Dictionary<string, int> { ["notes"] = 3 }, WholeDocumentRead: false);
        var rename = new BlockOp.ReplaceSectionOp("Progress", NewSectionH1: " # Results ");
        Assert.Equal("unread_block", Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc, partial, [rename])).Code);

        var result = _engine.ApplyCommit(doc, WholeAnchor(doc), [rename]);
        Assert.Equal(
            [doc.Blocks[0] with { SectionH1 = "Results", Rev = 4 }, doc.Blocks[1], doc.Blocks[2] with { SectionH1 = "Results", Rev = 6 }],
            result.NextDocument.Blocks);
        Assert.Equal(
            [new BlockChangeRecord("notes", ReportBlockKind.Prose, 3, 4, 0, Moved: false),
             new BlockChangeRecord("widget", ReportBlockKind.Widget, 5, 6, 2, Moved: false)],
            result.Changes);
    }

    [Theory]
    [InlineData("NOTES")]
    [InlineData("results")]
    public void ReplaceAndRenameSectionStripsEitherLeadingHeadingAndSupportsPlacement(string leadingHeading)
    {
        var doc = _engine.Parse("# Notes\n\n<!-- perga:notes rev=2 -->\nOld body.\n\n# Other\n\n<!-- perga:other -->\nOther body.");
        var result = _engine.ApplyCommit(doc, WholeAnchor(doc),
        [
            new BlockOp.ReplaceSectionOp("Notes", $"# {leadingHeading}\n\n<!-- perga:notes -->\nNew body.",
                NewSectionH1: "# Results", AfterBlockId: "other"),
            new BlockOp.ReplaceSectionOp("Inserted", "Inserted body.", BeforeBlockId: "other")
        ]);

        Assert.Equal(["Inserted", "Other", "Results"], result.NextDocument.Blocks.Select(block => block.SectionH1));
        Assert.Equal(doc.Blocks[0] with { SectionH1 = "Results", Markdown = "New body.", Rev = 3 }, result.NextDocument.Blocks[2]);
        Assert.Equal(new BlockChangeRecord("notes", ReportBlockKind.Prose, 2, 3, 2, Moved: true),
            Assert.Single(result.Changes, change => change.Id == "notes"));
    }

    [Fact]
    public void RenameSectionSupportsMovingASingleBlockBeforeAnother()
    {
        var doc = _engine.Parse("# First\n\n<!-- perga:first -->\nFirst body.\n\n# Second\n\n<!-- perga:second -->\nSecond body.");
        var result = _engine.ApplyCommit(doc, WholeAnchor(doc),
            [new BlockOp.ReplaceSectionOp("Second", NewSectionH1: "Renamed", BeforeBlockId: "first")]);

        Assert.Equal([doc.Blocks[1] with { SectionH1 = "Renamed", Rev = 2 }, doc.Blocks[0]], result.NextDocument.Blocks);
        Assert.Equal(new BlockChangeRecord("second", ReportBlockKind.Prose, 1, 2, 0, Moved: true), Assert.Single(result.Changes));
    }

    [Fact]
    public void ReplaceSectionRejectsInvalidAndStalePlacementAndMissingRename()
    {
        var doc = _engine.Parse("# Notes\n\n<!-- perga:notes -->\nBody.", docRev: 2);
        var whole = WholeAnchor(doc);
        var rename = new BlockOp.ReplaceSectionOp("Notes", NewSectionH1: "New Notes");

        Assert.Contains("'markdown' or 'newSectionH1'", Assert.Throws<ArgumentException>(() =>
            _engine.ApplyCommit(doc, whole, [new BlockOp.ReplaceSectionOp("Notes")])).Message);
        Assert.Contains("newSectionH1", Assert.Throws<ArgumentException>(() =>
            _engine.ApplyCommit(doc, whole, [rename with { NewSectionH1 = "\t" }])).Message);
        Assert.Contains("Provide 'markdown'", Assert.Throws<ArgumentException>(() =>
            _engine.ApplyCommit(doc, whole, [rename with { SectionH1 = "Missing" }])).Message);
        Assert.Contains("only one", Assert.Throws<ArgumentException>(() =>
            _engine.ApplyCommit(doc, whole, [rename with { BeforeBlockId = "one", AfterBlockId = "two" }])).Message);
        Assert.Contains("non-blank", Assert.Throws<ArgumentException>(() =>
            _engine.ApplyCommit(doc, whole, [rename with { BeforeBlockId = " " }])).Message);
        Assert.Contains("in section 'Notes'", Assert.Throws<ArgumentException>(() =>
            _engine.ApplyCommit(doc, whole, [rename with { AfterBlockId = "notes" }])).Message);
        Assert.Throws<KeyNotFoundException>(() =>
            _engine.ApplyCommit(doc, whole, [rename with { AfterBlockId = "missing" }]));

        var stale = new ReadAnchor(1, new Dictionary<string, int> { ["notes"] = 1, ["deleted"] = 1 }, WholeDocumentRead: true);
        Assert.Equal("block_deleted", Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc, stale, [rename with { BeforeBlockId = "deleted" }])).Code);
        Assert.Equal("stale_doc_rev", Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc, stale, [rename with { SectionH1 = "Missing" }])).Code);
    }

    [Fact]
    public void UpsertInsertsAtTheEndOfTheTargetSectionUnlessPositionIsSpecified()
    {
        var doc = _engine.Parse("""
            # Notes

            <!-- perga:first -->
            First.

            <!-- perga:last -->
            Last.

            # Other

            <!-- perga:other -->
            Other.
            """);
        var result = _engine.ApplyCommit(doc, WholeAnchor(doc),
        [
            new BlockOp.UpsertBlockOp("appended", null, "Appended.", null, SectionH1: "Notes"),
            new BlockOp.UpsertBlockOp("heading", null, "# Notes\n\nFrom heading.", null),
            new BlockOp.UpsertBlockOp("explicit", null, "Placed first.", null, Position: 0, SectionH1: "Notes"),
            new BlockOp.UpsertBlockOp("new", null, "New section.", null, SectionH1: "New")
        ]);

        var rendered = _engine.RenderMarkdown(result.NextDocument, withMarkers: true);
        Assert.Single(rendered.Split('\n'), line => line == "# Notes");
        var roundTrip = _engine.Parse(rendered);
        Assert.Equal(["explicit", "first", "last", "appended", "heading", "other", "new"],
            roundTrip.Blocks.Select(block => block.Id));
        Assert.Equal(["Notes", "Notes", "Notes", "Notes", "Notes", "Other", "New"],
            roundTrip.Blocks.Select(block => block.SectionH1));
    }

    [Fact]
    public void UpsertReassignsAnExistingBlockToTheEndOfItsTargetSection()
    {
        var doc = _engine.Parse("""
            # Source

            <!-- perga:moving rev=3 -->
            Move this paragraph.

            # Target

            <!-- perga:first -->
            First target paragraph.

            <!-- perga:last -->
            Last target paragraph.

            # Other

            <!-- perga:other -->
            Other paragraph.
            """);
        var result = _engine.ApplyCommit(doc, WholeAnchor(doc),
            [new BlockOp.UpsertBlockOp("moving", null, null, null, SectionH1: "Target")]);

        Assert.Equal(["first", "last", "moving", "other"], result.NextDocument.Blocks.Select(block => block.Id));
        Assert.Equal(doc.Blocks[0] with { SectionH1 = "Target", Rev = 4 }, result.NextDocument.Blocks[2]);
        Assert.Equal(new BlockChangeRecord("moving", ReportBlockKind.Prose, 3, 4, 2, Moved: true),
            Assert.Single(result.Changes));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void ReplaceSectionWithBlankMarkdownDeletesEveryBlockInTheSection(string markdown)
    {
        var doc = _engine.Parse("""
            # Notes

            <!-- perga:first rev=3 -->
            First note.

            # Other

            <!-- perga:other -->
            Other paragraph.

            # Notes

            <!-- perga:last rev=2 -->
            Last note.
            """, docRev: 5);
        var delete = new BlockOp.ReplaceSectionOp("Notes", markdown);
        var unread = new ReadAnchor(5, new Dictionary<string, int> { ["first"] = 3 }, WholeDocumentRead: false);
        var stale = unread with { KnownBlockRevs = new Dictionary<string, int> { ["first"] = 3, ["last"] = 1 } };

        Assert.Equal("unread_block", Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc, unread, [delete])).Code);
        Assert.Equal("stale_block_rev", Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc, stale, [delete])).Code);

        var result = _engine.ApplyCommit(doc, WholeAnchor(doc), [delete]);
        Assert.Equal(doc.Blocks[1], Assert.Single(result.NextDocument.Blocks));
        Assert.Equal(
            [new BlockChangeRecord("first", ReportBlockKind.Prose, 3, null, null, Moved: false),
             new BlockChangeRecord("last", ReportBlockKind.Prose, 2, null, null, Moved: false)],
            result.Changes);
        Assert.DoesNotContain("# Notes", _engine.RenderMarkdown(result.NextDocument, withMarkers: true));
    }

    [Fact]
    public void ReplaceSectionWithBlankMarkdownRejectsMissingSectionsAndContradictoryFields()
    {
        var doc = _engine.Parse("# Notes\n\nNotes body.", docRev: 5);
        var anchor = WholeAnchor(doc);
        var missing = new BlockOp.ReplaceSectionOp("Missing", "");

        Assert.StartsWith("Section 'Missing' does not exist.", Assert.Throws<ArgumentException>(() =>
            _engine.ApplyCommit(doc, anchor, [missing])).Message);
        Assert.Equal("stale_doc_rev", Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(doc, anchor with { DocRev = 4 }, [missing])).Code);

        var delete = new BlockOp.ReplaceSectionOp("Notes", "");
        foreach (var op in new[]
        {
            delete with { NewSectionH1 = "Renamed" },
            delete with { BeforeBlockId = "outside" },
            delete with { AfterBlockId = "outside" }
        })
        {
            Assert.StartsWith("Cannot combine empty 'markdown' (section deletion)", Assert.Throws<ArgumentException>(() =>
                _engine.ApplyCommit(doc, anchor, [op])).Message);
        }
    }

    [Fact]
    public void UpsertSectionAndPositionPreserveContentWhenKindAndContentAreOmitted()
    {
        var doc = _engine.Parse("# Notes\n\n<!-- perga:notes -->\nNotes body.");
        using var widgetPayload = JsonDocument.Parse("""{"use":"builtin:flow","view":"flow"}""");
        var inserted = _engine.ApplyCommit(doc, WholeAnchor(doc),
        [
            new BlockOp.UpsertBlockOp("widget", ReportBlockKind.Widget, null, widgetPayload.RootElement, SectionH1: "# Widget section"),
            new BlockOp.UpsertBlockOp("prose", null, "# Ignored heading\n\nProse body.", null, SectionH1: "Prose section")
        ]).NextDocument;
        Assert.Equal("Widget section", inserted.Blocks[1].SectionH1);
        Assert.Equal(ReportBlockKind.Prose, inserted.Blocks[2].Kind);
        Assert.Equal("Prose section", inserted.Blocks[2].SectionH1);
        Assert.Equal("Prose body.", inserted.Blocks[2].Markdown);

        var result = _engine.ApplyCommit(inserted, WholeAnchor(inserted),
        [
            new BlockOp.UpsertBlockOp("notes", null, null, null, SectionH1: " # New notes "),
            new BlockOp.UpsertBlockOp("widget", null, null, null, Position: 0, SectionH1: "New widgets"),
            new BlockOp.UpsertBlockOp("prose", null, null, null, Position: 1)
        ]);
        var blocks = result.NextDocument.Blocks;
        Assert.Equal(["widget", "prose", "notes"], blocks.Select(block => block.Id));
        Assert.Equal("New widgets", blocks[0].SectionH1);
        Assert.Equal(ReportBlockKind.Widget, blocks[0].Kind);
        Assert.True(JsonElement.DeepEquals(widgetPayload.RootElement, blocks[0].Payload!.Value));
        Assert.Equal("Prose body.", blocks[1].Markdown);
        Assert.Equal("New notes", blocks[2].SectionH1);
        Assert.Equal("Notes body.", blocks[2].Markdown);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SectionOperationsRejectMultilineTitles(string lineBreak)
    {
        var doc = _engine.Parse("# Notes\n\nBody.");
        var multiline = $" # First{lineBreak}Second ";
        var cases = new (BlockOp Op, string Field)[]
        {
            (new BlockOp.ReplaceSectionOp(multiline, "Body."), "sectionH1"),
            (new BlockOp.ReplaceSectionOp("Notes", NewSectionH1: multiline), "newSectionH1"),
            (new BlockOp.UpsertBlockOp(null, null, "Body.", null, SectionH1: multiline), "sectionH1")
        };

        foreach (var (op, field) in cases)
        {
            var error = Assert.Throws<ArgumentException>(() => _engine.ApplyCommit(doc, WholeAnchor(doc), [op]));
            Assert.Equal(field, error.ParamName);
            Assert.Contains($"'{field}' must be a single line.", error.Message);
        }
    }

    [Fact]
    public void SectionPlacementAndUpsertKeepUntitledBlocksInThePreamble()
    {
        var doc = _engine.Parse("""
            <!-- perga:first -->
            First preamble paragraph.

            <!-- perga:last -->
            Last preamble paragraph.

            # Notes

            <!-- perga:notes -->
            Notes body.
            """);
        var anchor = WholeAnchor(doc);
        var section = new BlockOp.ReplaceSectionOp("New", "<!-- perga:new -->\nNew body.");

        foreach (var op in new[]
        {
            section with { BeforeBlockId = "first" },
            new BlockOp.ReplaceSectionOp("Notes", NewSectionH1: "Renamed", AfterBlockId: "first")
        })
        {
            Assert.Contains("Cannot place an H1 section before an untitled preamble block.", Assert.Throws<ArgumentException>(
                () => _engine.ApplyCommit(doc, anchor, [op])).Message);
        }

        foreach (var op in new[]
        {
            new BlockOp.UpsertBlockOp(null, null, "New body.", null, Position: 0, SectionH1: "New"),
            new BlockOp.UpsertBlockOp("first", null, null, null, SectionH1: "New"),
            new BlockOp.UpsertBlockOp("notes", null, null, null, Position: 1)
        })
        {
            Assert.Contains("Cannot place an H1 section before an untitled preamble block.", Assert.Throws<ArgumentException>(
                () => _engine.ApplyCommit(doc, anchor, [op])).Message);
        }

        var placed = _engine.ApplyCommit(doc, anchor, [section with { AfterBlockId = "last" }]).NextDocument;
        var roundTrip = _engine.Parse(_engine.RenderMarkdown(placed, withMarkers: true));
        Assert.Equal(["first", "last", "new", "notes"], roundTrip.Blocks.Select(block => block.Id));
        Assert.Equal([null, null, "New", "Notes"], roundTrip.Blocks.Select(block => block.SectionH1));
        Assert.Equal(placed.Blocks.Select(block => block.Markdown), roundTrip.Blocks.Select(block => block.Markdown));

        var assigned = _engine.ApplyCommit(doc, anchor,
            [new BlockOp.UpsertBlockOp("first", null, null, null, Position: 1, SectionH1: "New")]).NextDocument;
        Assert.Equal(["last", "first", "notes"], assigned.Blocks.Select(block => block.Id));
        Assert.Equal([null, "New", "Notes"], assigned.Blocks.Select(block => block.SectionH1));
    }

    [Fact]
    public void UpsertRejectsMovingAnUntitledBlockAfterAnH1Section()
    {
        var doc = _engine.Parse("<!-- perga:intro -->\nIntroduction.\n\n# Notes\n\nNotes body.");
        var op = new BlockOp.UpsertBlockOp("intro", null, null, null, Position: 1);

        var error = Assert.Throws<ArgumentException>(() => _engine.ApplyCommit(doc, WholeAnchor(doc), [op]));

        Assert.Equal("ops", error.ParamName);
        Assert.Contains("Cannot place an untitled preamble block after an H1 section.", error.Message);
        Assert.Null(doc.Blocks[0].SectionH1);

        // Content-only upsert on an already-persisted [H1, null] layout still succeeds without moving or reassigning sections.
        var reordered = _engine.ApplyCommit(doc, WholeAnchor(doc), [new BlockOp.MoveBlockOp(doc.Blocks[1].Id, 0)]).NextDocument;
        var edited = _engine.ApplyCommit(reordered, WholeAnchor(reordered),
            [new BlockOp.UpsertBlockOp("intro", null, "Updated intro.", null)]).NextDocument;
        Assert.Equal("Updated intro.", edited.Blocks[1].Markdown);
    }

    [Fact]
    public void EditBlockNormalizesStoredCrLfMarkdownBeforeMatching()
    {
        var doc = new ReportDocument(1, "", "", [new ReportBlock("b_crlf", 1, ReportBlockKind.Prose, "Notes", "Line A\r\nLine B", null)]);
        var result = _engine.ApplyCommit(doc, WholeAnchor(doc), [new BlockOp.EditBlockOp("b_crlf", "Line A\r\nLine B", "Line A\nLine C")]);

        Assert.Equal("Line A\nLine C", Assert.Single(result.NextDocument.Blocks).Markdown);
        Assert.Equal(2, Assert.Single(result.NextDocument.Blocks).Rev);
    }

    [Fact]
    public void CrossSectionMoveRevisesTheMovedBlockAndRejectsAStaleRename()
    {
        var doc = _engine.Parse("""
            # Source

            <!-- perga:moving rev=3 -->
            Move this paragraph.

            # Target

            <!-- perga:first -->
            First target paragraph.

            <!-- perga:last -->
            Last target paragraph.
            """, docRev: 2);
        var anchor = new ReadAnchor(doc.DocRev, doc.Blocks.ToDictionary(block => block.Id, block => block.Rev), WholeDocumentRead: true);
        var moved = _engine.ApplyCommit(doc, anchor, [new BlockOp.MoveBlockOp("moving", 1)]);

        Assert.Equal("Target", moved.NextDocument.Blocks[1].SectionH1);
        Assert.Equal(new BlockChangeRecord("moving", ReportBlockKind.Prose, 3, 4, 1, Moved: true), Assert.Single(moved.Changes));
        var conflict = Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(moved.NextDocument, anchor, [new BlockOp.ReplaceSectionOp("Target", NewSectionH1: "Renamed")]));
        Assert.Equal("stale_block_rev", conflict.Code);
    }

    private static ReadAnchor WholeAnchor(ReportDocument doc) =>
        new(doc.DocRev, new Dictionary<string, int>(), WholeDocumentRead: true);

    [Fact]
    public void DecompressZstd_ReadsHistoricalBlockNamesAndOrdinals()
    {
        const string historicalJson = """
            [
              {"id":"task","rev":1,"kind":1,"payload":{"task_key":"old"}},
              {"id":"html","rev":1,"kind":"html","markdown":"<p>Retained</p>"},
              {"id":"table","rev":1,"kind":3,"payload":[]},
              {"id":"view","rev":1,"kind":"view","payload":{}},
              {"id":"checkpoints","rev":1,"kind":5,"payload":{"items":[]}},
              {"id":"widget","rev":1,"kind":"widget","payload":{"use":"builtin:flow"}}
            ]
            """;
        using var compressor = new ZstdSharp.Compressor();
        var blocks = _engine.DecompressZstd(compressor.Wrap(Encoding.UTF8.GetBytes(historicalJson)).ToArray());
        Assert.Equal(new[] { ReportBlockKind.Task, ReportBlockKind.Html, ReportBlockKind.Table, ReportBlockKind.View,
            ReportBlockKind.Checkpoints, ReportBlockKind.Widget }, blocks.Select(block => block.Kind));
        Assert.Equal("old", blocks[0].Payload!.Value.GetProperty("task_key").GetString());
        Assert.Equal(0, blocks[4].Payload!.Value.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void CompressZstd_And_DecompressZstd_RoundTripAllBlockKindsAndPayloads()
    {
        using var taskJson = JsonDocument.Parse("""{"key":"compile","dependsOn":["init"],"ready":true}""");
        using var tableJson = JsonDocument.Parse("""[{"col":"A","val":42},{"col":"B","val":99}]""");

        var blocks = new List<ReportBlock>
        {
            new("b_1001", 3, ReportBlockKind.Prose, "Overview", string.Concat(Enumerable.Repeat(" Perga Zstd snapshot compression test — 块快照验证。", 20)), null),
            new("b_1002", 1, ReportBlockKind.Task, "Tasks", null, taskJson.RootElement.Clone()),
            new("b_1003", 2, ReportBlockKind.Html, "Charts", "<section><h3>Return</h3></section>", null),
            new("b_1004", 1, ReportBlockKind.Table, "Data", null, tableJson.RootElement.Clone()),
            new("b_1005", 4, ReportBlockKind.View, null, "custom-fallback", null)
        };

        var rawJsonBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(blocks));
        var compressed = _engine.CompressZstd(blocks);

        // Verify Zstandard frame magic number (0xFD2FB528 little-endian: 28 B5 2F FD)
        Assert.True(compressed.Length >= 4);
        Assert.Equal(0x28, compressed[0]);
        Assert.Equal(0xB5, compressed[1]);
        Assert.Equal(0x2F, compressed[2]);
        Assert.Equal(0xFD, compressed[3]);
        Assert.True(compressed.Length < rawJsonBytes);

        var restored = _engine.DecompressZstd(compressed);
        Assert.Equal(blocks.Count, restored.Count);

        for (var i = 0; i < blocks.Count; i++)
        {
            Assert.Equal(blocks[i].Id, restored[i].Id);
            Assert.Equal(blocks[i].Rev, restored[i].Rev);
            Assert.Equal(blocks[i].Kind, restored[i].Kind);
            Assert.Equal(blocks[i].SectionH1, restored[i].SectionH1);
            Assert.Equal(blocks[i].Markdown, restored[i].Markdown);
            Assert.Equal(blocks[i].Payload?.GetRawText(), restored[i].Payload?.GetRawText());
        }

        // Empty list round-trip
        var emptyCompressed = _engine.CompressZstd([]);
        Assert.Empty(_engine.DecompressZstd(emptyCompressed));
    }

    [Fact]
    public void NestedBackticksInStructuredBlocks_ManyBlocksUniqueIds_AndMultiBlockSectionLocks_WorkCleanly()
    {
        using var fencedGoalPayload = JsonDocument.Parse("""
            {
              "key": "fix-fence",
              "goal": "Ensure ```csharp\nConsole.WriteLine();\n``` inside task JSON survives round-trip"
            }
            """);

        var initial = new ReportDocument(
            DocRev: 0,
            Summary: "init",
            LeadingComment: string.Empty,
            Blocks:
            [
                new ReportBlock("b_f001", 1, ReportBlockKind.Task, "Tasks", null, fencedGoalPayload.RootElement.Clone())
            ]);

        var rendered = _engine.RenderMarkdown(initial, withMarkers: true);
        Assert.Contains("````perga-block task", rendered);

        var reparsed = _engine.Parse(rendered);
        Assert.Single(reparsed.Blocks);
        Assert.Equal("b_f001", reparsed.Blocks[0].Id);
        Assert.Contains("```csharp", reparsed.Blocks[0].Payload?.GetProperty("goal").GetString());

        // Multi-block ReplaceSectionOp requires KnownBlockRevs for EVERY block in the replaced section when WholeDocumentRead=false
        var multiSecDoc = _engine.Parse("""
            # Multi

            <!-- perga:b_aa01 -->
            Part 1

            <!-- perga:b_aa02 -->
            ```perga-block html
            <div>Part 2</div>
            ```
            """, docRev: 2);

        // Partial KnownBlockRevs (missing b_aa02) -> throws ReportConflictException
        var partialEx = Assert.Throws<ReportConflictException>(() =>
            _engine.ApplyCommit(
                multiSecDoc,
                new ReadAnchor(1, new Dictionary<string, int> { ["b_aa01"] = 1 }, WholeDocumentRead: false),
                [new BlockOp.ReplaceSectionOp("Multi", "Unified replacement")]));
        Assert.Equal("unread_block", partialEx.Code);

        // Complete KnownBlockRevs for all blocks in # Multi -> succeeds
        var fullSecPass = _engine.ApplyCommit(
            multiSecDoc,
            new ReadAnchor(1, new Dictionary<string, int> { ["b_aa01"] = 1, ["b_aa02"] = 1 }, WholeDocumentRead: false),
            [new BlockOp.ReplaceSectionOp("# Multi", "Unified replacement")]);
        Assert.Equal(3, fullSecPass.NextDocument.DocRev);
        Assert.Single(fullSecPass.NextDocument.Blocks);
        Assert.Equal("Unified replacement", fullSecPass.NextDocument.Blocks[0].Markdown);

        // Duplicate unmarked blocks still receive distinct 6-char b_xxxx IDs
        var sb = new StringBuilder("# Repeated\n\n");
        for (var i = 0; i < 30; i++)
        {
            sb.AppendLine("Identical paragraph.");
            sb.AppendLine();
            sb.AppendLine("```perga-block task");
            sb.AppendLine("{\"key\":\"dup\"}");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        var manyDoc = _engine.Parse(sb.ToString());
        Assert.Equal(60, manyDoc.Blocks.Count);
        Assert.Equal(60, manyDoc.Blocks.Select(b => b.Id).Distinct().Count());
        Assert.All(manyDoc.Blocks, b => Assert.Matches(SixCharBlockIdRegex(), b.Id));
    }
}
