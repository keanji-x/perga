using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Perga.Contracts;
using Perga.Kernel;

namespace Perga.Host.Http;

/// <summary>Reads bounded sections directly into memory before request admission.</summary>
internal static class MultipartImageInput
{
    public const string PayloadField = "payload";
    public const string AttachmentsField = "attachments";
    public const int MaxPayloadBytes = 1024 * 1024;
    private const long MaxRequestBytes = ImageAttachmentContract.MaxTotalBytes + MaxPayloadBytes + 256 * 1024;

    public static bool IsMultipart(HttpRequest request) =>
        MediaTypeHeaderValue.TryParse(request.ContentType, out var type) &&
        string.Equals(type.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase);

    public static async Task<(T Payload, IReadOnlyList<byte[]> Images)> ReadAsync<T>(
        HttpContext context, JsonSerializerOptions jsonOptions, CancellationToken ct)
    {
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        var maxRequestBytes = Math.Min(limit?.MaxRequestBodySize ?? MaxRequestBytes, MaxRequestBytes);
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = maxRequestBytes;
        if (context.Request.ContentLength > maxRequestBytes)
            throw TooLarge();

        var contentType = MediaTypeHeaderValue.Parse(context.Request.ContentType!);
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128)
            throw KernelException.InvalidArgument("A multipart boundary of at most 128 characters is required.");

        var reader = new MultipartReader(boundary, context.Request.Body);
        var images = new List<byte[]>();
        byte[]? payload = null;
        long imageBytes = 0;
        try
        {
            while (await reader.ReadNextSectionAsync(ct).ConfigureAwait(false) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                    throw KernelException.InvalidArgument("Invalid multipart content disposition.");
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
                if (name == AttachmentsField && isFile)
                {
                    if (images.Count == ImageAttachmentContract.MaxAttachmentsPerMessage)
                        throw KernelException.InvalidArgument($"A message can carry at most {ImageAttachmentContract.MaxAttachmentsPerMessage} images.");
                    var bytes = await ReadBoundedAsync(section.Body,
                        Math.Min(ImageAttachmentContract.MaxAttachmentBytes, ImageAttachmentContract.MaxTotalBytes - imageBytes), ct)
                        .ConfigureAwait(false);
                    imageBytes += bytes.Length;
                    images.Add(bytes);
                }
                else if (name == PayloadField && !isFile && payload is null)
                {
                    payload = await ReadBoundedAsync(section.Body, MaxPayloadBytes, ct).ConfigureAwait(false);
                }
                else
                {
                    throw KernelException.InvalidArgument("Use one payload JSON field and attachments file fields.");
                }
            }
        }
        catch (BadHttpRequestException)
        {
            // Preserve Kestrel's status, including a streaming request exceeding its body limit.
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is InvalidDataException or IOException))
        {
            throw KernelException.InvalidArgument("Invalid multipart body.");
        }

        if (payload is null || images.Count == 0)
            throw KernelException.InvalidArgument("One payload JSON field and at least one image attachment are required.");
        var body = JsonSerializer.Deserialize<T>(payload, jsonOptions)
            ?? throw KernelException.InvalidArgument("Request payload cannot be null.");
        return (body, images);
    }

    private static KernelException TooLarge() =>
        new(KernelErrorCode.PayloadTooLarge, "The multipart request exceeds the size limit.");

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - output.Length + 1)), ct).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maxBytes) throw TooLarge();
            output.Write(buffer, 0, count);
        }
    }
}
