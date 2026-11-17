using System.Text.Json;

namespace Perga.ReportDoc;

public enum ReportBlockKind
{
    Prose,
    Task,
    Html,
    Table,
    View
}

public sealed record ReportBlock(
    string Id,
    int Rev,
    ReportBlockKind Kind,
    string? SectionH1,
    string? Markdown,
    JsonElement? Payload);

public sealed record ReadAnchor(
    int DocRev,
    IReadOnlyDictionary<string, int> KnownBlockRevs,
    bool WholeDocumentRead);

public abstract record BlockOp
{
    public sealed record ReplaceSectionOp(string SectionH1, string Markdown) : BlockOp;
    public sealed record UpsertBlockOp(
        string? Id,
        ReportBlockKind Kind,
        string? Markdown,
        JsonElement? Payload,
        int? Position = null) : BlockOp;
    public sealed record DeleteBlockOp(string Id) : BlockOp;
    public sealed record MoveBlockOp(string Id, int ToIndex) : BlockOp;
}

public sealed record ReportDocument(
    int DocRev,
    string Summary,
    string LeadingComment,
    IReadOnlyList<ReportBlock> Blocks);

public sealed record BlockChangeRecord(
    string Id,
    ReportBlockKind Kind,
    int? PreviousRev,
    int? CurrentRev,
    int? Position,
    bool Moved);

public sealed record ReportCommitResult(
    ReportDocument NextDocument,
    IReadOnlyList<BlockChangeRecord> Changes);

public sealed class ReportConflictException : Exception
{
    public string Code { get; }
    public string NextStep { get; }

    public ReportConflictException(string code, string message, string nextStep)
        : base(message)
    {
        Code = code;
        NextStep = nextStep;
    }
}

public interface IReportDocEngine
{
    ReportDocument Parse(string rawMarkdown, int docRev = 0, string summary = "");
    string RenderMarkdown(ReportDocument doc, bool withMarkers = false);
    ReportCommitResult ApplyCommit(
        ReportDocument current,
        ReadAnchor callerAnchor,
        IReadOnlyList<BlockOp> ops,
        string? newSummary = null);
    byte[] CompressZstd(IReadOnlyList<ReportBlock> blocks);
    IReadOnlyList<ReportBlock> DecompressZstd(byte[] compressed);
}
