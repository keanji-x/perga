using System.Text;
using Perga.Contracts;

namespace Perga.Kernel;

public sealed record TrackOverlayPreviewCandidates(string? Ask = null, string? BlockedSessionError = null,
    string? WorkspaceError = null, string? Reply = null, string? Summary = null);

/// <summary>Shared selection and text bound for stored and live Track overlay previews.</summary>
public static class TrackOverlayPreview
{
    public const int MaxLength = 280;

    public static (string? Text, TrackOverlayPreviewKind? Kind) Select(TrackOverlayPreviewCandidates candidates, bool isOpen)
    {
        if (isOpen)
        {
            if (Normalize(candidates.Ask) is { } ask) return (ask, TrackOverlayPreviewKind.Ask);
            if ((Normalize(candidates.BlockedSessionError) ?? Normalize(candidates.WorkspaceError)) is { } error)
                return (error, TrackOverlayPreviewKind.Error);
        }
        if (Normalize(candidates.Reply) is { } reply) return (reply, TrackOverlayPreviewKind.Reply);
        if (Normalize(candidates.Summary) is { } summary) return (summary, TrackOverlayPreviewKind.Summary);
        return (null, null);
    }

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
