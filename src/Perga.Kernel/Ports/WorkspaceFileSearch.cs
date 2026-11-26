using System.Globalization;
using System.Text;
using Perga.Contracts;

namespace Perga.Kernel.Ports;

public static class WorkspaceFileSearch
{
    public const int ScanLimit = 20_000;
    // A query can find files beyond the initial index while keeping the same time budget.
    public const int QueryScanLimit = 200_000;
    public const int ScanTimeoutSeconds = 3;

    public static IReadOnlyList<string> SkippedDirectories { get; } =
        Array.AsReadOnly<string>([".git", "node_modules", "bin", "obj", "dist", ".venv"]);

    public static IReadOnlyList<string> GitRepositoryOverrides { get; } =
        Array.AsReadOnly<string>(["GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE"]);

    public static WorkspaceFileSearchResultDto Rank(
        IEnumerable<string> paths, bool scanTruncated, string? query, int limit)
    {
        limit = Math.Clamp(limit, 1, WorkspaceFileSearchResultDto.MaxLimit);
        var filter = new Query(query);
        var matches = paths.Distinct(StringComparer.Ordinal)
            .Select(path => (Path: path, Score: filter.Score(path)))
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

    public sealed class Query
    {
        private readonly Term[][] _groups;

        public Query(string? query)
        {
            var raw = (query ?? string.Empty).Normalize().TrimStart();
            Literal = raw.TrimEnd();
            var pattern = Literal.EndsWith('\\') && raw.Length > Literal.Length && raw[Literal.Length] == ' '
                ? Literal + " " : Literal;
            // Protect escaped spaces while splitting the extended query into terms.
            var tokens = pattern.Replace("\\ ", "\t", StringComparison.Ordinal)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var groups = new List<List<Term>>();
            var joinNext = false;
            foreach (var token in tokens)
            {
                if (token == "|" && groups.Count > 0 && !joinNext)
                {
                    joinNext = true;
                    continue;
                }

                var term = Term.Parse(token.Replace('\t', ' '));
                if (term.Text.Length == 0) continue;
                if (!joinNext) groups.Add([]);
                groups[^1].Add(term);
                joinNext = false;
            }
            _groups = groups.Select(group => group.ToArray()).ToArray();
        }

        public string Literal { get; }

        // The remote adapter may discard ASCII paths that cannot satisfy a positive group.
        // It leaves negation, non-ASCII paths and final ranking to this filter.
        public string[][] AsciiPrefilterGroups() => _groups
            .Where(group => group.All(term => !term.Inverse && Ascii.IsValid(term.Text)))
            .Select(group => group.Select(term => term.Text).ToArray())
            .ToArray();

        internal int Score(string path)
        {
            if (Literal.Length == 0) return 0;

            var normalized = Ascii.IsValid(path) ? path : path.Normalize();
            if (normalized == Literal || normalized.AsSpan(normalized.LastIndexOf('/') + 1).SequenceEqual(Literal))
                return 0;

            string? folded = null;
            // Reserve zero for the literal path or basename, even for inverse-only queries.
            var total = 1;
            foreach (var group in _groups)
            {
                var best = int.MaxValue;
                foreach (var term in group)
                {
                    var candidate = term.Fold ? folded ??= FoldDiacritics(normalized) : normalized;
                    var score = term.Score(candidate);
                    if (score >= 0) best = Math.Min(best, score);
                }
                if (best == int.MaxValue) return -1;
                total += best;
            }
            return total;
        }
    }

    private enum MatchKind { Fuzzy, Contains, Prefix, Suffix, Equal }

    private readonly record struct Term(string Text, MatchKind Kind, bool Inverse, bool Fold, bool SmartCase)
    {
        internal static Term Parse(string text)
        {
            var inverse = text.StartsWith('!');
            if (inverse) text = text[1..];
            var kind = inverse ? MatchKind.Contains : MatchKind.Fuzzy;
            // fzf's parseTerms strips the suffix marker before the quote can override suffix matching.
            if (text.Length > 1 && text.EndsWith('$'))
            {
                kind = MatchKind.Suffix;
                text = text[..^1];
            }
            if (text.StartsWith('\''))
            {
                kind = inverse ? MatchKind.Fuzzy : MatchKind.Contains;
                text = text[1..];
            }
            else if (text.StartsWith('^'))
            {
                kind = kind == MatchKind.Suffix ? MatchKind.Equal : MatchKind.Prefix;
                text = text[1..];
            }

            return new Term(text, kind, inverse, text == FoldDiacritics(text), text != text.ToLowerInvariant());
        }

        internal int Score(string path)
        {
            var comparison = SmartCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var score = MatchScore(path, comparison);
            if (Inverse) return score < 0 ? 0 : -1;
            if (score >= 0 || !SmartCase) return score;

            // Keep case-insensitive matches behind all exact-case match kinds (0–4).
            score = MatchScore(path, StringComparison.OrdinalIgnoreCase);
            return score < 0 ? -1 : score + 10;
        }

        private int MatchScore(string path, StringComparison comparison)
        {
            if (Kind == MatchKind.Fuzzy) return ScoreFuzzy(path, Text, comparison);

            var candidate = path.AsSpan();
            if (Kind is MatchKind.Prefix or MatchKind.Equal && !char.IsWhiteSpace(Text[0]))
                candidate = candidate.TrimStart();
            if (Kind is MatchKind.Suffix or MatchKind.Equal && !char.IsWhiteSpace(Text[^1]))
                candidate = candidate.TrimEnd();
            var matched = Kind switch
            {
                MatchKind.Contains => candidate.Contains(Text, comparison),
                MatchKind.Prefix => candidate.StartsWith(Text, comparison),
                MatchKind.Suffix => candidate.EndsWith(Text, comparison),
                _ => candidate.Equals(Text, comparison)
            };
            return matched ? ScoreFuzzy(path, Text, comparison) : -1;
        }
    }

    private static string FoldDiacritics(string value)
    {
        if (Ascii.IsValid(value)) return value;
        var folded = new StringBuilder(value.Length);
        foreach (var rune in value.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark) continue;
            folded.Append(rune.Value switch
            {
                'ł' => "l", 'Ł' => "L",
                'đ' or 'ð' => "d", 'Đ' or 'Ð' => "D",
                'ø' => "o", 'Ø' => "O",
                'þ' => "t", 'Þ' => "T",
                'ß' => "s",
                _ => rune.ToString()
            });
        }
        return folded.ToString().Normalize();
    }

    private static int ScoreFuzzy(string path, string query, StringComparison comparison)
    {
        var name = path.AsSpan(path.LastIndexOf('/') + 1);
        if (name.Equals(query, comparison)) return 0;
        if (name.StartsWith(query, comparison)) return 1;

        foreach (var segment in path.AsSpan().Split('/'))
        {
            if (path.AsSpan()[segment.Start..].StartsWith(query, comparison))
                return 2;
        }

        if (path.Contains(query, comparison)) return 3;
        var matched = 0;
        foreach (var character in path)
        {
            if ((character == query[matched] || (comparison == StringComparison.OrdinalIgnoreCase &&
                char.ToUpperInvariant(character) == char.ToUpperInvariant(query[matched]))) && ++matched == query.Length)
                return 4;
        }
        return -1;
    }
}
