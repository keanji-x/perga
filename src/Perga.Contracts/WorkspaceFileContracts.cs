using System.Text.Json.Serialization;

namespace Perga.Contracts;

public sealed record WorkspaceFileSearchResultDto(IReadOnlyList<string> Paths, bool Truncated)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 20_000;
}

public sealed record WorkspaceFileOpenRequestedDto(
    string TrackId,
    string SessionId,
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Line,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? EndLine);

public sealed record WorkspaceFileOpenResultDto(
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Line,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? EndLine,
    bool Opened);
