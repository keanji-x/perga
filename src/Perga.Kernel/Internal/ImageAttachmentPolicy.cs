using System.Security.Cryptography;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Internal;

internal static class ImageAttachmentPolicy
{
    public static IReadOnlyList<ImageMediaWrite> Validate(IReadOnlyList<byte[]>? images)
    {
        if (images is not { Count: > 0 }) return [];
        if (images.Count > ImageAttachmentContract.MaxAttachmentsPerMessage)
            throw KernelException.InvalidArgument($"A message can carry at most {ImageAttachmentContract.MaxAttachmentsPerMessage} images.");

        long total = 0;
        var validated = new List<ImageMediaWrite>(images.Count);
        foreach (var bytes in images)
        {
            if (bytes is not { Length: > 0 })
                throw KernelException.InvalidArgument("Image attachments cannot be empty.");
            total += bytes.LongLength;
            if (bytes.LongLength > ImageAttachmentContract.MaxAttachmentBytes || total > ImageAttachmentContract.MaxTotalBytes)
                throw new KernelException(KernelErrorCode.PayloadTooLarge, "Image attachments exceed the size limit.");
            if (!ImageAttachmentContract.TryDetectImageFormat(bytes, out var contentType, out var extension))
                throw KernelException.InvalidArgument("Only PNG, JPEG, GIF, and WebP images are supported.");

            var id = $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}.{extension}";
            validated.Add(new(new(id, contentType, bytes.LongLength), bytes));
        }
        return validated;
    }
}
