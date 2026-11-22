using System.Buffers;
using System.Text;

namespace Perga.Host.Auth;

internal static class AuthCredentials
{
    public static string? NormalizeUsername(string? value)
    {
        value = value?.Trim();
        if (value is null || value.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '@' or '-')))
            return null;
        return value.ToLowerInvariant();
    }

    public static bool IsValidPassword(string? value, int minimumScalars)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048)
            return false;

        var remaining = value.AsSpan();
        var scalars = 0;
        var bytes = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done || rune.Value == 0)
                return false;

            scalars++;
            bytes += rune.Utf8SequenceLength;
            remaining = remaining[consumed..];
        }
        return scalars >= minimumScalars && scalars <= 1024 && bytes <= 4096;
    }
}
