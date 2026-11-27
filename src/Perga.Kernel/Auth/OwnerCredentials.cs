using System.Buffers;
using System.Text;

namespace Perga.Kernel.Auth;

public static class OwnerCredentials
{
    public const int MinOwnerPasswordScalars = 12;
    public const string UsernameError = "Username must contain 1–64 ASCII characters, start with a letter or digit, and use only letters, digits, dots, underscores, @ or hyphens.";
    public const string DisplayNameError = "Display name must contain 1–128 characters and no control characters.";
    public static string PasswordError => $"Password must contain {MinOwnerPasswordScalars}–1024 Unicode characters, no NUL and at most 4096 UTF-8 bytes.";

    public static bool TryNormalizeDisplayName(string? value, out string? displayName)
    {
        displayName = value?.Trim();
        return displayName is null || displayName.Length is >= 1 and <= 128 && !displayName.Any(char.IsControl);
    }

    public static string? NormalizeUsername(string? value)
    {
        value = value?.Trim();
        if (value is null || value.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '@' or '-')))
            return null;
        return value.ToLowerInvariant();
    }

    public static bool IsValidPassword(string? value, int minimumScalars = MinOwnerPasswordScalars)
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
