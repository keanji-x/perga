using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ZstdSharp;

namespace Perga.ReportDoc;

public sealed partial class ReportDocEngine : IReportDocEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [GeneratedRegex(@"^\s*<!--\s*perga:([A-Za-z0-9_-]+)(?:\s+rev=(\d+))?\s*-->\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLineRegex();

    [GeneratedRegex(@"^#\s+(.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex H1HeadingRegex();

    [GeneratedRegex(@"^\s*(`{3,})perga-block\s+(task|html|table|view|checkpoints|widget)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PergaBlockOpenRegex();

    [GeneratedRegex(@"^\s*(`{3,}|~{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex GenericCodeFenceRegex();

    public ReportDocument Parse(string rawMarkdown, int docRev = 0, string summary = "")
    {
        ArgumentNullException.ThrowIfNull(rawMarkdown);
        ArgumentNullException.ThrowIfNull(summary);

        var normalized = rawMarkdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var (leadingComment, remainder) = ExtractLeadingComment(normalized);

        var blocks = ParseBlocks(remainder, existingIds: null);
        return new ReportDocument(docRev, summary, leadingComment, blocks);
    }

    public string RenderMarkdown(ReportDocument doc, bool withMarkers = false)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var chunks = new List<string>();
        if (!string.IsNullOrWhiteSpace(doc.LeadingComment))
        {
            chunks.Add(doc.LeadingComment.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n'));
        }

        string? emittedSection = null;
        foreach (var block in doc.Blocks)
        {
            if (!string.Equals(block.SectionH1, emittedSection, StringComparison.Ordinal))
            {
                if (block.SectionH1 is not null)
                {
                    chunks.Add($"# {block.SectionH1}");
                }

                emittedSection = block.SectionH1;
            }

            if (block.Kind == ReportBlockKind.Prose)
            {
                var body = NormalizeBlockText(block.Markdown);
                if (withMarkers)
                {
                    chunks.Add(string.IsNullOrEmpty(body)
                        ? $"<!-- perga:{block.Id} -->"
                        : $"<!-- perga:{block.Id} -->\n{body}");
                }
                else if (!string.IsNullOrEmpty(body))
                {
                    chunks.Add(body);
                }
            }
            else
            {
                var kindName = block.Kind.ToString().ToLowerInvariant();
                var body = block.Payload is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } payload
                    ? payload.GetRawText()
                    : NormalizeBlockText(block.Markdown);

                var fence = BuildSafeBacktickFence(body);
                var fenced = string.IsNullOrEmpty(body)
                    ? $"{fence}perga-block {kindName}\n{fence}"
                    : $"{fence}perga-block {kindName}\n{body}\n{fence}";

                chunks.Add(withMarkers
                    ? $"<!-- perga:{block.Id} -->\n{fenced}"
                    : fenced);
            }
        }

        return string.Join("\n\n", chunks);
    }

    public byte[] CompressZstd(IReadOnlyList<ReportBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(blocks, JsonOptions);
        using var compressor = new Compressor(level: 3);
        return compressor.Wrap(utf8Json).ToArray();
    }

    public IReadOnlyList<ReportBlock> DecompressZstd(byte[] compressed)
    {
        ArgumentNullException.ThrowIfNull(compressed);

        using var input = new MemoryStream(compressed, writable: false);
        using var decompressionStream = new DecompressionStream(input);
        using var output = new MemoryStream();
        decompressionStream.CopyTo(output);

        var deserialized = JsonSerializer.Deserialize<List<ReportBlock>>(output.GetBuffer().AsSpan(0, (int)output.Length), JsonOptions)
            ?? [];

        // Ensure every Payload JsonElement is independently cloned.
        for (var i = 0; i < deserialized.Count; i++)
        {
            if (deserialized[i].Payload is { } p)
            {
                deserialized[i] = deserialized[i] with { Payload = p.Clone() };
            }
        }

        return deserialized;
    }

    private static (string LeadingComment, string Remainder) ExtractLeadingComment(string normalized)
    {
        var idx = 0;
        var len = normalized.Length;

        while (idx < len && (normalized[idx] == ' ' || normalized[idx] == '\t' || normalized[idx] == '\n'))
        {
            idx++;
        }

        var comments = new List<string>();
        var cursor = idx;

        while (cursor < len && normalized.AsSpan(cursor).StartsWith("<!--", StringComparison.Ordinal))
        {
            var closeIdx = normalized.IndexOf("-->", cursor + 4, StringComparison.Ordinal);
            if (closeIdx < 0)
            {
                break;
            }

            var commentEnd = closeIdx + 3;
            var commentSlice = normalized[cursor..commentEnd];

            // Stop if this HTML comment is actually a perga block marker.
            var inner = normalized[(cursor + 4)..closeIdx].Trim();
            if (inner.StartsWith("perga:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            comments.Add(commentSlice);
            cursor = commentEnd;
            while (cursor < len && (normalized[cursor] == ' ' || normalized[cursor] == '\t' || normalized[cursor] == '\n'))
            {
                cursor++;
            }
        }

        if (comments.Count == 0)
        {
            return (string.Empty, normalized);
        }

        return (string.Join("\n", comments), normalized[cursor..]);
    }

    private static List<ReportBlock> ParseBlocks(string markdown, HashSet<string>? existingIds, int seedOffset = 0)
    {
        var blocks = new List<ReportBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        var usedIds = existingIds is not null
            ? new HashSet<string>(existingIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var lines = markdown.Split('\n');
        string? currentSectionH1 = null;
        var sectionHasEmittedBlock = false;
        string? pendingMarkerId = null;
        int? pendingMarkerRev = null;
        var proseLines = new List<string>();

        var inProseFence = false;
        var proseFenceChar = '`';
        var proseFenceLen = 0;
        var occurrenceCounter = seedOffset;

        void EmitProse(string text, bool allowEmpty)
        {
            if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            occurrenceCounter++;
            var id = ResolveBlockId(
                pendingMarkerId,
                ReportBlockKind.Prose,
                currentSectionH1,
                text,
                occurrenceCounter,
                usedIds);
            var rev = pendingMarkerRev ?? 1;
            blocks.Add(new ReportBlock(id, rev, ReportBlockKind.Prose, currentSectionH1, text, null));
            sectionHasEmittedBlock = true;
            pendingMarkerId = null;
            pendingMarkerRev = null;
        }

        void FlushAccumulatedProse()
        {
            var trimmed = TrimBlankLines(proseLines);
            proseLines.Clear();
            if (!string.IsNullOrEmpty(trimmed))
            {
                EmitProse(trimmed, allowEmpty: false);
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (inProseFence)
            {
                proseLines.Add(line);
                if (IsClosingFence(line, proseFenceChar, proseFenceLen))
                {
                    inProseFence = false;
                }

                continue;
            }

            var markerMatch = MarkerLineRegex().Match(line);
            if (markerMatch.Success)
            {
                var trimmedProse = TrimBlankLines(proseLines);
                proseLines.Clear();
                if (!string.IsNullOrEmpty(trimmedProse))
                {
                    EmitProse(trimmedProse, allowEmpty: false);
                }
                else if (pendingMarkerId is not null)
                {
                    EmitProse(string.Empty, allowEmpty: true);
                }

                pendingMarkerId = markerMatch.Groups[1].Value;
                pendingMarkerRev = markerMatch.Groups[2].Success && int.TryParse(markerMatch.Groups[2].Value, out var r)
                    ? r
                    : null;
                continue;
            }

            var h1Match = H1HeadingRegex().Match(line);
            if (h1Match.Success)
            {
                var trimmedProse = TrimBlankLines(proseLines);
                proseLines.Clear();
                if (!string.IsNullOrEmpty(trimmedProse))
                {
                    EmitProse(trimmedProse, allowEmpty: false);
                }
                else if (currentSectionH1 is not null && !sectionHasEmittedBlock)
                {
                    EmitProse(string.Empty, allowEmpty: true);
                }

                currentSectionH1 = h1Match.Groups[1].Value.Trim();
                sectionHasEmittedBlock = false;
                continue;
            }

            var pergaFenceMatch = PergaBlockOpenRegex().Match(line);
            if (pergaFenceMatch.Success)
            {
                FlushAccumulatedProse();

                var openBackticks = pergaFenceMatch.Groups[1].Value.Length;
                var kind = ParseBlockKind(pergaFenceMatch.Groups[2].Value);
                var fenceLines = new List<string>();
                i++;
                while (i < lines.Length && !IsClosingFence(lines[i], '`', openBackticks))
                {
                    fenceLines.Add(lines[i]);
                    i++;
                }

                var inner = string.Join("\n", fenceLines);
                var (blockMarkdown, blockPayload) = ParseStructuredBlockContent(inner);

                occurrenceCounter++;
                var id = ResolveBlockId(
                    pendingMarkerId,
                    kind,
                    currentSectionH1,
                    inner,
                    occurrenceCounter,
                    usedIds);
                var rev = pendingMarkerRev ?? 1;

                blocks.Add(new ReportBlock(id, rev, kind, currentSectionH1, blockMarkdown, blockPayload));
                sectionHasEmittedBlock = true;
                pendingMarkerId = null;
                pendingMarkerRev = null;
                continue;
            }

            var genericFenceMatch = GenericCodeFenceRegex().Match(line);
            if (genericFenceMatch.Success)
            {
                var fenceToken = genericFenceMatch.Groups[1].Value;
                inProseFence = true;
                proseFenceChar = fenceToken[0];
                proseFenceLen = fenceToken.Length;
            }

            proseLines.Add(line);
        }

        var trailingProse = TrimBlankLines(proseLines);
        proseLines.Clear();
        if (!string.IsNullOrEmpty(trailingProse))
        {
            EmitProse(trailingProse, allowEmpty: false);
        }
        else if (pendingMarkerId is not null || (currentSectionH1 is not null && !sectionHasEmittedBlock))
        {
            EmitProse(string.Empty, allowEmpty: true);
        }

        return blocks;
    }

    private static (string? Markdown, JsonElement? Payload) ParseStructuredBlockContent(string inner)
    {
        var trimmed = inner.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '{' && trimmed[^1] == '}') || (trimmed[0] == '[' && trimmed[^1] == ']')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return (null, doc.RootElement.Clone());
                }
            }
            catch (JsonException)
            {
                // Fall back to storing raw content in Markdown (e.g. non-JSON html/template content).
            }
        }

        return (inner, null);
    }

    private static ReportBlockKind ParseBlockKind(string rawKind) =>
        rawKind.ToLowerInvariant() switch
        {
            "task" => ReportBlockKind.Task,
            "html" => ReportBlockKind.Html,
            "table" => ReportBlockKind.Table,
            "view" => ReportBlockKind.View,
            "checkpoints" => ReportBlockKind.Checkpoints,
            "widget" => ReportBlockKind.Widget,
            _ => ReportBlockKind.Prose
        };

    private static string ResolveBlockId(
        string? explicitMarkerId,
        ReportBlockKind kind,
        string? sectionH1,
        string content,
        int occurrenceIndex,
        HashSet<string> usedIds)
    {
        if (!string.IsNullOrWhiteSpace(explicitMarkerId) && usedIds.Add(explicitMarkerId))
        {
            return explicitMarkerId;
        }

        return AllocateDeterministicId(kind, sectionH1, content, occurrenceIndex, usedIds);
    }

    private static string AllocateDeterministicId(
        ReportBlockKind kind,
        string? sectionH1,
        string content,
        int occurrenceIndex,
        HashSet<string> usedIds)
    {
        Span<byte> hash = stackalloc byte[32];
        var attempt = 0;
        while (true)
        {
            var material = $"{sectionH1 ?? string.Empty}\0{kind}\0{content}\0{occurrenceIndex}\0{attempt}";
            SHA256.HashData(Encoding.UTF8.GetBytes(material), hash);
            var candidate = $"b_{hash[0]:x2}{hash[1]:x2}";
            if (usedIds.Add(candidate))
            {
                return candidate;
            }

            attempt++;
        }
    }

    private static bool IsClosingFence(string line, char fenceChar, int minLength)
    {
        var span = line.AsSpan().TrimStart();
        var count = 0;
        while (count < span.Length && span[count] == fenceChar)
        {
            count++;
        }

        if (count < minLength)
        {
            return false;
        }

        return span[count..].IsWhiteSpace();
    }

    private static string BuildSafeBacktickFence(string body)
    {
        var maxRun = 0;
        var currentRun = 0;
        foreach (var ch in body)
        {
            if (ch == '`')
            {
                currentRun++;
                if (currentRun > maxRun)
                {
                    maxRun = currentRun;
                }
            }
            else
            {
                currentRun = 0;
            }
        }

        return new string('`', Math.Max(3, maxRun + 1));
    }

    private static string NormalizeBlockText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return TrimBlankLines(NormalizeLineEndings(text).Split('\n').ToList());
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string TrimBlankLines(List<string> lines)
    {
        var start = 0;
        var end = lines.Count - 1;

        while (start <= end && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        while (end >= start && string.IsNullOrWhiteSpace(lines[end]))
        {
            end--;
        }

        if (start > end)
        {
            return string.Empty;
        }

        return string.Join("\n", lines.GetRange(start, end - start + 1));
    }
}
