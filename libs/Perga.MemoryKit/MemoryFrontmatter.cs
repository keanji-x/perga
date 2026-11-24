using System.Text;
using System.Text.Json;

namespace Perga.MemoryKit;

// The memory schema uses one-line YAML scalars and string sequences.
// Accept common handwritten forms as well as the quoted form produced here.
internal sealed class MemoryFrontmatter
{
    internal string? Title { get; private set; }
    internal string? Summary { get; private set; }
    internal bool Pinned { get; private set; }
    internal List<string> Tags { get; } = [];
    internal List<string> Sources { get; } = [];

    internal static string Write(
        string title, string summary, bool pinned, IReadOnlyList<string> tags, IReadOnlyList<string> sources)
    {
        var output = new StringBuilder("---\n");
        output.Append("title: ").Append(Quote(title)).Append('\n');
        output.Append("summary: ").Append(Quote(summary)).Append('\n');
        output.Append("pinned: ").Append(pinned ? "true\n" : "false\n");
        WriteList(output, "tags", tags);
        if (sources.Count > 0)
            WriteList(output, "sources", sources);
        return output.Append("---\n").ToString();
    }

    private static void WriteList(StringBuilder output, string key, IReadOnlyList<string> values)
    {
        output.Append(key).Append(':');
        if (values.Count == 0)
        {
            output.Append(" []\n");
            return;
        }

        output.Append('\n');
        foreach (var value in values)
            output.Append("  - ").Append(Quote(value)).Append('\n');
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal static MemoryFrontmatter Read(string content, out string body)
    {
        var result = new MemoryFrontmatter();
        var start = content.StartsWith('\uFEFF') ? 1 : 0;
        body = start == 0 ? content : content[start..];
        var position = start;
        if (!ReadLine(content, ref position).TrimEnd().SequenceEqual("---"))
            return result;

        var headerStart = position;
        while (position < content.Length)
        {
            var lineStart = position;
            if (!ReadLine(content, ref position).TrimEnd().SequenceEqual("---"))
                continue;

            if (!result.ReadFields(content.AsSpan(headerStart, lineStart - headerStart)))
                return new MemoryFrontmatter();
            body = content[position..];
            break;
        }

        return result;
    }

    private static ReadOnlySpan<char> ReadLine(string text, ref int position)
    {
        var remaining = text.AsSpan(position);
        var length = remaining.IndexOfAny('\r', '\n');
        if (length < 0)
        {
            position = text.Length;
            return remaining;
        }

        position += length + 1;
        if (remaining[length] == '\r' && position < text.Length && text[position] == '\n')
            position++;
        return remaining[..length];
    }

    private bool ReadFields(ReadOnlySpan<char> header)
    {
        List<string>? blockSequence = null;
        var hasMapping = false;
        foreach (var field in header.EnumerateLines())
        {
            var line = field.Trim();
            if (line.IsEmpty || line[0] == '#')
                continue;

            if (blockSequence is not null && line.Length > 1 && line[0] == '-' && char.IsWhiteSpace(line[1]))
            {
                var item = ParseScalar(line[1..]);
                if (item is not null)
                    blockSequence.Add(item);
                continue;
            }

            if (char.IsWhiteSpace(field[0]))
                continue;

            blockSequence = null;
            var colon = line.IndexOf(':');
            if (colon <= 0 || (colon + 1 < line.Length && !char.IsWhiteSpace(line[colon + 1])))
                return false;
            hasMapping = true;
            var value = line[(colon + 1)..].Trim();
            switch (line[..colon].TrimEnd())
            {
                case "title":
                    Title = ParseMetadataLine(value);
                    break;
                case "summary":
                    Summary = ParseMetadataLine(value);
                    break;
                case "pinned":
                    Pinned = bool.TryParse(PlainScalar(value), out var pinned) && pinned;
                    break;
                case "tags":
                    blockSequence = ReadSequence(value, Tags);
                    break;
                case "sources":
                    blockSequence = ReadSequence(value, Sources);
                    break;
            }
        }
        return hasMapping;
    }

    private static List<string>? ReadSequence(ReadOnlySpan<char> value, List<string> items)
    {
        items.Clear();
        if (value.IsEmpty || value[0] == '#')
            return items;

        if (value[0] == '[' && ParseFlowSequence(value[1..]) is { } sequence)
            items.AddRange(sequence);
        return null;
    }

    private static List<string>? ParseFlowSequence(ReadOnlySpan<char> value)
    {
        var items = new List<string>();
        while (true)
        {
            value = value.TrimStart();
            if (value.IsEmpty)
                return null;
            if (value[0] == ']')
                return HasOnlyComment(value[1..]) ? items : null;

            string? item;
            if (value[0] is '\'' or '"')
            {
                item = TakeQuoted(ref value);
            }
            else
            {
                var length = value.IndexOfAny(',', ']');
                if (length < 0)
                    return null;
                item = ParseScalar(value[..length]);
                value = value[length..];
            }

            if (item is null)
                return null;
            items.Add(item);
            value = value.TrimStart();
            if (!value.IsEmpty && value[0] == ',')
                value = value[1..];
            else if (value.IsEmpty || value[0] != ']')
                return null;
        }
    }

    private static string? ParseMetadataLine(ReadOnlySpan<char> value)
    {
        var scalar = ParseScalar(value);
        if (scalar is null)
            return null;
        var singleLine = MemoryMarkdown.CleanLine(scalar);
        return singleLine.Length > 0 ? singleLine : null;
    }

    private static string? ParseScalar(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        if (value.IsEmpty)
            return null;
        if (value[0] is not ('\'' or '"'))
        {
            var plain = PlainScalar(value);
            return plain.IsEmpty || plain[0] is '[' or '{' or '|' or '>' ? null : plain.ToString();
        }

        var quoted = TakeQuoted(ref value);
        return HasOnlyComment(value) ? quoted : null;
    }

    private static string? TakeQuoted(ref ReadOnlySpan<char> value)
    {
        var quote = value[0];
        for (var i = 1; i < value.Length; i++)
        {
            if ((quote == '"' && value[i] == '\\')
                || (quote == '\'' && value[i] == '\'' && i + 1 < value.Length && value[i + 1] == '\''))
            {
                i++;
                continue;
            }

            if (value[i] != quote)
                continue;

            var scalar = value[..(i + 1)];
            value = value[(i + 1)..];
            if (quote == '\'')
                return scalar[1..^1].ToString().Replace("''", "'", StringComparison.Ordinal);

            try
            {
                return JsonSerializer.Deserialize<string>(scalar.ToString());
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static ReadOnlySpan<char> PlainScalar(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '#' && (i == 0 || char.IsWhiteSpace(value[i - 1])))
                return value[..i].TrimEnd();
        }
        return value.TrimEnd();
    }

    private static bool HasOnlyComment(ReadOnlySpan<char> value)
    {
        var trimmed = value.TrimStart();
        return trimmed.IsEmpty || trimmed[0] == '#';
    }
}
