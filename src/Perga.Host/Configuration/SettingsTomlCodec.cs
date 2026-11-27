using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Perga.Host.Configuration;

/// <summary>Single-level tables and scalar values for settings.toml, using TOML 1.0 syntax.</summary>
internal static partial class SettingsTomlCodec
{
    public static IReadOnlyDictionary<string, object> Decode(string text)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        string? table = null;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].AsSpan();
            if (i < lines.Length - 1 && line.EndsWith("\r"))
                line = line[..^1];
            line = line.Trim(" \t");

            try
            {
                if (line.IsEmpty || line[0] == '#')
                {
                    ReadEnd(line);
                    continue;
                }

                if (line[0] == '[')
                {
                    var close = line.IndexOf(']');
                    if (close < 0) throw new FormatException("Expected a closing table bracket.");
                    var name = line[1..close].Trim(" \t");
                    if (!IsBareKey(name)) throw new FormatException("Expected a single bare table name.");
                    ReadEnd(line[(close + 1)..]);
                    table = name.ToString();
                    if (!tables.Add(table)) throw new FormatException($"Duplicate table '{table}'.");
                    continue;
                }

                if (table is null) throw new FormatException("Expected a table before any settings.");
                var equals = line.IndexOf('=');
                if (equals < 0) throw new FormatException("Expected a key and an equals sign.");
                var key = line[..equals].Trim(" \t");
                if (!IsBareKey(key)) throw new FormatException("Expected a bare key.");
                var field = string.Concat(table, ".", key);
                var value = ReadValue(line[(equals + 1)..].TrimStart(" \t"));
                if (!values.TryAdd(field, value)) throw new FormatException($"Duplicate key '{field}'.");
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Line {i + 1}: {ex.Message}", ex);
            }
        }

        return values;
    }

    public static string Encode(IReadOnlyDictionary<string, object> values)
    {
        var result = new StringBuilder();
        string? table = null;
        foreach (var (field, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var dot = field.IndexOf('.');
            if (dot < 0 || !IsBareKey(field.AsSpan(0, dot)) || !IsBareKey(field.AsSpan(dot + 1)))
                throw new FormatException($"Invalid settings key '{field}'.");

            var section = field[..dot];
            if (section != table)
            {
                if (table is not null) result.Append('\n');
                result.Append('[').Append(section).Append("]\n");
                table = section;
            }

            result.Append(field.AsSpan(dot + 1)).Append(" = ");
            result.Append(value switch
            {
                string str => Quote(str),
                int number => number.ToString(CultureInfo.InvariantCulture),
                long number => number.ToString(CultureInfo.InvariantCulture),
                bool boolean => boolean ? "true" : "false",
                _ => throw new FormatException($"Unsupported setting type for '{field}'.")
            }).Append('\n');
        }

        return result.ToString();
    }

    private static object ReadValue(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) throw new FormatException("Expected a value.");

        if (value[0] is '"' or '\'')
            return ReadString(value);

        var comment = value.IndexOf('#');
        if (comment >= 0)
        {
            ReadEnd(value[comment..]);
            value = value[..comment];
        }

        value = value.TrimEnd(" \t");
        if (value.SequenceEqual("true")) return true;
        if (value.SequenceEqual("false")) return false;
        if (DecimalInteger().IsMatch(value) &&
            long.TryParse(value.ToString().Replace("_", "", StringComparison.Ordinal), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var number))
            return number;

        throw new FormatException("Expected a quoted string, signed decimal integer or boolean.");
    }

    private static string ReadString(ReadOnlySpan<char> value)
    {
        var quote = value[0];
        var result = new StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == quote)
            {
                ReadEnd(value[(i + 1)..]);
                return result.ToString();
            }

            if (IsForbiddenControl(ch)) throw new FormatException("Control character in string.");
            if (ch != '\\' || quote == '\'')
            {
                result.Append(ch);
                continue;
            }

            if (++i == value.Length) break;
            var escape = value[i];
            if (escape is 'u' or 'U')
            {
                var length = escape == 'u' ? 4 : 8;
                if (i + length >= value.Length)
                    throw new FormatException("Invalid Unicode escape.");
                var digits = value.Slice(i + 1, length);
                foreach (var digit in digits)
                    if (!char.IsAsciiHexDigit(digit))
                        throw new FormatException("Invalid Unicode escape.");
                if (!int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var scalar) ||
                    !Rune.TryCreate(scalar, out var rune))
                    throw new FormatException("Invalid Unicode escape.");

                result.Append(rune.ToString());
                i += length;
                continue;
            }

            result.Append(escape switch
            {
                'b' => '\b', 't' => '\t', 'n' => '\n', 'f' => '\f', 'r' => '\r',
                '"' => '"', '\\' => '\\',
                _ => throw new FormatException("Unknown string escape.")
            });
        }

        throw new FormatException("Unterminated string.");
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder().Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\t': result.Append("\\t"); break;
                case '\n': result.Append("\\n"); break;
                case '\f': result.Append("\\f"); break;
                case '\r': result.Append("\\r"); break;
                default:
                    if (IsForbiddenControl(ch))
                        result.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        result.Append(ch);
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    private static void ReadEnd(ReadOnlySpan<char> rest)
    {
        rest = rest.TrimStart(" \t");
        if (rest.IsEmpty) return;
        if (rest[0] != '#') throw new FormatException("Unexpected text at the end of the line.");
        foreach (var ch in rest)
            if (IsForbiddenControl(ch)) throw new FormatException("Control character in comment.");
    }

    private static bool IsForbiddenControl(char ch) => (ch < ' ' && ch != '\t') || ch == '\u007f';

    private static bool IsBareKey(ReadOnlySpan<char> value) => BareKey().IsMatch(value);

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\z")]
    private static partial Regex BareKey();

    [GeneratedRegex(@"\A[+-]?(?:0|[1-9](?:_?[0-9])*)\z")]
    private static partial Regex DecimalInteger();
}
