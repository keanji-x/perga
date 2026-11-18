namespace Perga.ReportDoc.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void ReportDocContracts_ConstructCleanly()
    {
        var block = new ReportBlock("b_a1b2", 1, ReportBlockKind.Prose, "Overview", "Hello", null);
        var doc = new ReportDocument(1, "init", "", [block]);
        var anchor = new ReadAnchor(1, new Dictionary<string, int> { ["b_a1b2"] = 1 }, true);
        var ex = new ReportConflictException("conflict", "stale rev", "read again");

        Assert.Single(doc.Blocks);
        Assert.True(anchor.WholeDocumentRead);
        Assert.Equal("conflict", ex.Code);
    }
}
