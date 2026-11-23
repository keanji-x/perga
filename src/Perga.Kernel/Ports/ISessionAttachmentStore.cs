using System.Text.RegularExpressions;
using Perga.Contracts;

namespace Perga.Kernel.Ports;

public sealed record StoredSessionAttachment(
    string Id,
    string? TrackId,
    string? SessionId,
    string Status,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? OriginalFilename,
    string StorageRelPath,
    long CreatedAtMs,
    long? BoundAtMs,
    string? PhysicalPath = null);

public sealed record AttachmentUploadLimits(
    long MaxFileBytes = 10_485_760L,
    int MaxPerMessage = 8,
    long MaxSessionBytes = 67_108_864L,
    long MaxUnownedStagedBytes = 67_108_864L,
    int MaxUnownedStagedCount = 64);

public interface ISessionAttachmentStore
{
    AttachmentUploadLimits Limits { get; }

    Task<StoredSessionAttachment> StoreStagedAsync(
        string? trackId,
        string? sessionId,
        Stream content,
        string? originalFilename,
        CancellationToken ct);

    Task<StoredSessionAttachment?> GetForSessionAsync(
        string sessionId,
        string attachmentId,
        CancellationToken ct);

    Task<StoredSessionAttachment?> GetUnownedStagedAsync(
        string attachmentId,
        CancellationToken ct);

    Task<Stream?> OpenReadStreamAsync(
        StoredSessionAttachment attachment,
        CancellationToken ct);
}

public static partial class SessionAttachmentValidation
{
    public const int DefaultMaxPerMessage = 8;

    [GeneratedRegex("^att_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentIdRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256HexRegex();

    public static bool IsValidAttachmentId(string? id) =>
        !string.IsNullOrEmpty(id) && AttachmentIdRegex().IsMatch(id);

    public static bool IsValidSha256(string? sha256) =>
        !string.IsNullOrEmpty(sha256) && Sha256HexRegex().IsMatch(sha256);

    public static bool IsValidTrackSegment(string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return false;
        var trimmed = trackId.Trim();
        return trimmed is not ("." or ".." or "_staged") &&
               trimmed.IndexOfAny(['/', '\\', '\0']) < 0;
    }

    public static string? ExtensionForContentType(string? contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => null
    };

    public static bool IsAllowedExtension(string? extension) =>
        extension is ".png" or ".jpg" or ".gif" or ".webp";

    public static IReadOnlyList<string>? NormalizeAndValidateIds(
        IReadOnlyList<string>? rawIds,
        int maxPerMessage = DefaultMaxPerMessage)
    {
        if (rawIds is null || rawIds.Count == 0)
        {
            return null;
        }

        if (rawIds.Count > maxPerMessage)
        {
            throw KernelException.InvalidArgument($"At most {maxPerMessage} attachments are allowed per message.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new string[rawIds.Count];
        for (var i = 0; i < rawIds.Count; i++)
        {
            var id = rawIds[i]?.Trim();
            if (!IsValidAttachmentId(id))
            {
                throw KernelException.InvalidArgument($"Invalid attachment ID '{rawIds[i]}'.");
            }

            if (!seen.Add(id!))
            {
                throw KernelException.InvalidArgument($"Duplicate attachment ID '{id}' is not allowed.");
            }

            normalized[i] = id!;
        }

        return normalized;
    }

    public static string ForSessionUrl(string sessionId, string attachmentId) =>
        $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/attachments/{Uri.EscapeDataString(attachmentId)}";

    public static string ForStagedUrl(string attachmentId) =>
        $"/api/v1/attachments/{Uri.EscapeDataString(attachmentId)}";

    public static SessionAttachmentDto ToSessionDto(StoredSessionAttachment attachment) =>
        new(
            attachment.Id,
            attachment.ContentType,
            attachment.SizeBytes,
            attachment.SessionId is { Length: > 0 } sid
                ? ForSessionUrl(sid, attachment.Id)
                : ForStagedUrl(attachment.Id));

    public static UploadAttachmentResponseDto ToUploadResponse(StoredSessionAttachment attachment) =>
        new(
            attachment.Id,
            attachment.ContentType,
            attachment.SizeBytes,
            attachment.SessionId is { Length: > 0 } sid
                ? ForSessionUrl(sid, attachment.Id)
                : ForStagedUrl(attachment.Id));
}
