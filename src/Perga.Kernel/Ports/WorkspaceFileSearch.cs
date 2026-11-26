using Perga.Contracts;

namespace Perga.Kernel.Ports;

public static class WorkspaceFileSearch
{
    public const int ScanLimit = 20_000;
    public const int ScanTimeoutSeconds = 3;

    public static IReadOnlyList<string> SkippedDirectories { get; } =
        Array.AsReadOnly<string>([".git", "node_modules", "bin", "obj", "dist", ".venv"]);

    public static IReadOnlyList<string> GitRepositoryOverrides { get; } =
        Array.AsReadOnly<string>(["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]);

    public static WorkspaceFileSearchResultDto Rank(
        IEnumerable<string> paths, bool scanTruncated, string? query, int limit)
    {
        limit = Math.Clamp(limit, 1, WorkspaceFileSearchResultDto.MaxLimit);
        var needle = query?.Trim() ?? string.Empty;
        var matches = paths.Distinct(StringComparer.Ordinal)
            .Select(path => (Path: path, Score: Score(path, needle)))
            .Where(match => match.Score >= 0)
            .OrderBy(match => match.Score)
            .ThenBy(match => match.Path.Length)
            .ThenBy(match => match.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Path, StringComparer.Ordinal)
            .Take(limit + 1)
            .Select(match => match.Path)
            .ToArray();
        return new WorkspaceFileSearchResultDto(matches.Take(limit).ToArray(), scanTruncated || matches.Length > limit);
    }

    private static int Score(string path, string query)
    {
        if (query.Length == 0) return 0;
        var name = path.AsSpan(path.LastIndexOf('/') + 1);
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;

        foreach (var segment in path.AsSpan().Split('/'))
        {
            if (path.AsSpan()[segment.Start..].StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return 2;
        }

        if (path.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        var matched = 0;
        foreach (var character in path)
        {
            if (char.ToUpperInvariant(character) == char.ToUpperInvariant(query[matched]) && ++matched == query.Length)
                return 4;
        }
        return -1;
    }
}
