namespace Perga.Contracts;

public sealed record MessageAttachmentDto(string Id, string ContentType, long Size, string Url);

public sealed record StoredImageAttachment(string Id, string ContentType, long Size)
{
    public MessageAttachmentDto ToDto(string trackId) =>
        new(Id, ContentType, Size, ImageAttachmentContract.BuildMediaUrl(trackId, Id));
}

public static class ImageAttachmentContract
{
    public const int MaxAttachmentsPerMessage = 8;
    public const long MaxAttachmentBytes = 10L * 1024 * 1024;
    public const long MaxTotalBytes = 25L * 1024 * 1024;

    public static IReadOnlyList<string> MediaTypes { get; } =
        Array.AsReadOnly(new[] { "image/png", "image/jpeg", "image/gif", "image/webp" });

    public static bool TryDetectImageFormat(ReadOnlySpan<byte> bytes, out string contentType, out string ext)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            contentType = "image/png";
            ext = "png";
            return true;
        }

        // JPEG: FF D8 FF
        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            contentType = "image/jpeg";
            ext = "jpg";
            return true;
        }

        // GIF87a / GIF89a
        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a')
        {
            contentType = "image/gif";
            ext = "gif";
            return true;
        }

        // WebP: RIFF....WEBP
        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            contentType = "image/webp";
            ext = "webp";
            return true;
        }

        contentType = string.Empty;
        ext = string.Empty;
        return false;
    }

    public static bool TryParseMediaFileName(string? fileName, out string contentType)
    {
        contentType = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var dot = fileName.IndexOf('.', StringComparison.Ordinal);
        if (dot != 64 || fileName.IndexOf('.', dot + 1) >= 0)
        {
            return false;
        }

        for (var i = 0; i < 64; i++)
        {
            var ch = fileName[i];
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
            {
                return false;
            }
        }

        var ext = fileName[(dot + 1)..];
        contentType = ext switch
        {
            "png" => "image/png",
            "jpg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            _ => string.Empty
        };
        return contentType.Length > 0;
    }

    public static string BuildMediaUrl(string trackId, string id) =>
        $"/api/v1/tracks/{Uri.EscapeDataString(trackId)}/media/{id}";
}
