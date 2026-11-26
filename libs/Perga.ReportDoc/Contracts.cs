using System.Text.Json;

namespace Perga.ReportDoc;

public enum ReportBlockKind
{
    // Historical report revisions may contain either these ordinals or their names.
    Prose = 0,
    Task = 1,
    Html = 2,
    Table = 3,
    View = 4,
    Checkpoints = 5,
    Widget = 6
}

public sealed record ReportBlock(
    string Id,
    int Rev,
    ReportBlockKind Kind,
    string? SectionH1,
    string? Markdown,
    JsonElement? Payload)
{
    public bool HasSameContentAs(ReportBlock other) =>
        Kind == other.Kind && SectionH1 == other.SectionH1 && Markdown == other.Markdown &&
        (Payload is { } payload
            ? other.Payload is { } otherPayload && JsonElement.DeepEquals(payload, otherPayload)
            : other.Payload is null);
}

public sealed record ReadAnchor(
    int DocRev,
    IReadOnlyDictionary<string, int> KnownBlockRevs,
    bool WholeDocumentRead);

public abstract record BlockOp
{
    public sealed record ReplaceSectionOp(
        string SectionH1,
        string? Markdown = null,
        string? NewSectionH1 = null,
        string? BeforeBlockId = null,
        string? AfterBlockId = null) : BlockOp;
    public sealed record EditBlockOp(string Id, string Target, string Replacement) : BlockOp;
    public sealed record UpsertBlockOp(
        string? Id,
        ReportBlockKind? Kind,
        string? Markdown,
        JsonElement? Payload,
        int? Position = null,
        string? SectionH1 = null) : BlockOp;
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
