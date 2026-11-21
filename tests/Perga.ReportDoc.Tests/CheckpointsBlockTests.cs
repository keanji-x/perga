using System.Text.Json;
using Perga.ReportDoc;

namespace Perga.ReportDoc.Tests;

public sealed class CheckpointsBlockTests
{
    [Fact]
    public void ParseAndRender_PreservesCheckpointsBlockAndPayload()
    {
        var engine = new ReportDocEngine();
        var raw = """
            # Target

            Validate native checkpoints block parsing.

            # Stages

            ```perga-block checkpoints
            {"tier":"AGENTS.md · L2","items":[{"id":"design","title":"设计","status":"done","docPath":"docs/design/checkpoint-native-view.md"}]}
            ```
            """;

        var doc = engine.Parse(raw);
        Assert.Equal(2, doc.Blocks.Count);
        Assert.Equal(ReportBlockKind.Prose, doc.Blocks[0].Kind);
        Assert.Equal(ReportBlockKind.Checkpoints, doc.Blocks[1].Kind);
        Assert.Equal("Stages", doc.Blocks[1].SectionH1);
        Assert.True(doc.Blocks[1].Payload.HasValue);
        Assert.Equal("AGENTS.md · L2", doc.Blocks[1].Payload!.Value.GetProperty("tier").GetString());

        var rendered = engine.RenderMarkdown(doc, withMarkers: true);
        Assert.Contains("```perga-block checkpoints", rendered, StringComparison.Ordinal);

        var reparsed = engine.Parse(rendered);
        Assert.Equal(doc.Blocks[1].Id, reparsed.Blocks[1].Id);
        Assert.Equal(ReportBlockKind.Checkpoints, reparsed.Blocks[1].Kind);
    }

    [Fact]
    public void ParseAndRender_PreservesWidgetBlockAndPayload()
    {
        var engine = new ReportDocEngine();
        var raw = """
            # Price

            ```perga-block widget
            {"use":"./widgets/range-axis.html","view":"valuationAxis","chrome":"seamless"}
            ```
            """;

        var doc = engine.Parse(raw);
        Assert.Single(doc.Blocks);
        Assert.Equal(ReportBlockKind.Widget, doc.Blocks[0].Kind);
        Assert.Equal("./widgets/range-axis.html", doc.Blocks[0].Payload!.Value.GetProperty("use").GetString());

        var rendered = engine.RenderMarkdown(doc, withMarkers: true);
        Assert.Contains("```perga-block widget", rendered, StringComparison.Ordinal);

        var reparsed = engine.Parse(rendered);
        Assert.Equal(ReportBlockKind.Widget, reparsed.Blocks[0].Kind);
    }
}
