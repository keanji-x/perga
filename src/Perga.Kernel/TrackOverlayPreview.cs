using System.Text;

namespace Perga.Kernel;

/// <summary>Shared text bound for stored and live Track overlay previews.</summary>
public static class TrackOverlayPreview
{
    public const int MaxLength = 280;

    public static string? Normalize(string? text)
    {
        if (text is null) return null;

        var preview = new StringBuilder(Math.Min(text.Length, MaxLength));
        var needsSpace = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                needsSpace = preview.Length > 0;
                continue;
            }

            if (preview.Length + (needsSpace ? 1 : 0) + rune.Utf16SequenceLength > MaxLength) break;
            if (needsSpace) preview.Append(' ');
            preview.Append(rune);
            needsSpace = false;
        }

        return preview.Length == 0 ? null : preview.ToString();
    }
}
