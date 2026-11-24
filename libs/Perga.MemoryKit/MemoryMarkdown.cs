using System.Security.Cryptography;
using System.Text;

namespace Perga.MemoryKit;

internal sealed record MemoryFile(MemoryEntry Entry, string Body, int ByteCount)
{
    internal MemoryEntryMeta Meta => Entry.Meta;
}

internal static class MemoryMarkdown
{
    internal const int MaxFileBytes = 16 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static (byte[] Bytes, MemoryFile File) Prepare(MemoryRef reference, MemoryPutRequest request)
    {
        var title = RequireLine(request.Title, "Title", 80);
        var summary = RequireLine(request.Summary, "Summary", 140);
        var tags = NormalizeTags(ValidateList(request.Tags, "Tags", 8, 32, nonEmpty: true));
        var sources = ValidateList(request.Sources, "Sources", 16, 128, nonEmpty: false);
        if (request.Body is null)
            throw new MemoryValidationException("A Markdown body is required.");

        try
        {
            if (Utf8.GetByteCount(request.Body) > MaxFileBytes)
                throw new MemoryValidationException($"The Markdown body exceeds {MaxFileBytes} UTF-8 bytes.");

            var content = MemoryFrontmatter.Write(title, summary, request.Pinned, tags, sources) + request.Body;
            var bytes = Utf8.GetBytes(content);
            if (bytes.Length > MaxFileBytes)
                throw new MemoryValidationException($"The Markdown file including frontmatter exceeds {MaxFileBytes} UTF-8 bytes.");

            var meta = new MemoryEntryMeta(reference, title, summary, request.Pinned, tags, sources, 0, Hash(bytes));
            return (bytes, new MemoryFile(new MemoryEntry(meta, content), request.Body, bytes.Length));
        }
        catch (EncoderFallbackException)
        {
            throw new MemoryValidationException("Memory Markdown and metadata must be valid UTF-8 text.");
        }
    }

    internal static MemoryFile? Read(MemoryRef reference, byte[] bytes, DateTime updatedAtUtc)
    {
        string content;
        try
        {
            content = Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var frontmatter = MemoryFrontmatter.Read(content, out var body);
        var (title, summary) = FallbackMetadata(reference.Key, body, frontmatter);
        var meta = new MemoryEntryMeta(reference, title, summary, frontmatter.Pinned,
            NormalizeTags(frontmatter.Tags), frontmatter.Sources.AsReadOnly(),
            new DateTimeOffset(updatedAtUtc).ToUnixTimeMilliseconds(), Hash(bytes));
        return new MemoryFile(new MemoryEntry(meta, content), body, bytes.Length);
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static (string Title, string Summary) FallbackMetadata(string key, string body, MemoryFrontmatter frontmatter)
    {
        if (frontmatter.Title is { } title && frontmatter.Summary is { } completeSummary)
            return (title, completeSummary);

        var visible = StripHtmlComments(body);
        var summary = frontmatter.Summary;
        foreach (var line in visible.AsSpan().EnumerateLines())
        {
            if (summary is not null)
                break;

            var clean = CleanFallbackLine(line);
            if (clean.Length > 0)
                summary = Truncate(clean, 140);
        }

        return (frontmatter.Title ?? FindHeading(visible) ?? key, summary ?? "");
    }

    private static string StripHtmlComments(string body)
    {
        var start = body.IndexOf("<!--", StringComparison.Ordinal);
        if (start < 0)
            return body;

        var visible = new StringBuilder(body.Length);
        var offset = 0;
        while (start >= 0)
        {
            visible.Append(body, offset, start - offset);
            var end = body.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0)
                return visible.ToString();
            offset = end + 3;
            start = body.IndexOf("<!--", offset, StringComparison.Ordinal);
        }
        return visible.Append(body, offset, body.Length - offset).ToString();
    }

    private static string CleanFallbackLine(ReadOnlySpan<char> line) =>
        CleanLine(string.Join(' ', line.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

    private static string? FindHeading(string body)
    {
        char fence = default;
        var fenceLength = 0;
        foreach (var raw in body.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.IsEmpty)
                continue;

            var marker = line[0];
            var markerCount = marker is '\u0060' or '~' ? line.Length - line.TrimStart(marker).Length : 0;
            if (fenceLength > 0)
            {
                if (marker == fence && markerCount >= fenceLength && markerCount == line.Length)
                    fenceLength = 0;
                continue;
            }
            if (raw.Length - raw.TrimStart().Length > 3 || raw[0] == '\t')
                continue;
            if (markerCount >= 3)
            {
                fence = marker;
                fenceLength = markerCount;
                continue;
            }
            if (line.Length > 2 && marker == '#' && line[1] is ' ' or '\t')
            {
                var heading = line[2..].TrimStart();
                var beforeClosingHashes = heading.TrimEnd('#');
                if (!beforeClosingHashes.IsEmpty && char.IsWhiteSpace(beforeClosingHashes[^1]))
                    heading = beforeClosingHashes;
                return CleanFallbackLine(heading);
            }
        }

        return null;
    }

    internal static string CleanLine(ReadOnlySpan<char> value)
    {
        var trimmed = value.Trim();
        return trimmed.ContainsAny('\u2028', '\u2029') || trimmed.ContainsAnyInRange('\u0000', '\u001f')
            || trimmed.ContainsAnyInRange('\u007f', '\u009f')
            ? string.Concat(trimmed.ToString().Select(c => IsLineCharacter(c) ? c : ' ')).Trim()
            : trimmed.ToString();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;
        return value[..(char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength)];
    }

    private static bool IsLineCharacter(char c) => !char.IsControl(c) && c is not ('\u2028' or '\u2029');

    private static string RequireLine(string? value, string label, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || !value.All(IsLineCharacter))
            throw new MemoryValidationException($"{label} must contain 1 to {maxLength} characters on one line without control characters.");
        return value.Trim();
    }

    private static IReadOnlyList<string> ValidateList(
        IReadOnlyList<string>? values, string label, int maxCount, int maxLength, bool nonEmpty)
    {
        if (values is null)
            return [];
        if (values.Count > maxCount)
            throw new MemoryValidationException($"{label} can contain at most {maxCount} values.");

        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value is null || (nonEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maxLength || !value.All(IsLineCharacter))
                throw new MemoryValidationException($"Each {label.ToLowerInvariant()} value must fit on one line of at most {maxLength} characters.");
            result.Add(value);
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) =>
        Array.AsReadOnly(tags.Select(tag => CleanLine(tag).ToLowerInvariant())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray());
}
