using System.Text.Json.Serialization;

namespace Perga.AgentKit.Internal;

internal sealed record EncodedTurnImage(
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("data")] string Data);

internal static class TurnAttachmentEncoder
{
    public static async Task<EncodedTurnImage[]> EncodeAsync(IReadOnlyList<TurnImage> attachments, CancellationToken ct)
    {
        var images = new EncodedTurnImage[attachments.Count];
        for (var i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];
            try
            {
                await using var stream = await attachment.OpenAsync(ct).ConfigureAwait(false)
                    ?? throw new FileNotFoundException();
                if (attachment.Size <= 0 || stream.Length != attachment.Size)
                    throw new InvalidDataException("Attachment size changed.");
                var bytes = new byte[attachment.Size];
                await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                images[i] = new(attachment.ContentType, Convert.ToBase64String(bytes));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AttachmentDispatchException("attachment_unavailable",
                    "A saved image attachment could not be read.", ex);
            }
        }
        return images;
    }
}
