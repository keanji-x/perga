using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    internal static string HashMessage(string? content, string mode, string? turnId, int? expectedEpoch,
        IReadOnlyList<ImageMediaWrite> images)
    {
        var identity = new { version = 4, Content = content, Mode = mode, TurnId = turnId, ExpectedEpoch = expectedEpoch };
        return images.Count == 0 ? RequestIdentity.Hash(identity)
            : RequestIdentity.Hash(new { identity, attachments = images.Select(image => image.Attachment.Id) });
    }

    private bool ReadAttachmentSupportCore(AgentSessionDto session)
    {
        if (_runtimes.TryGetOpen(session.Id, out var handle) &&
            SessionLaunchValidator.NormalizeProvider(handle.LaunchSpec.Provider) == session.Provider)
            return handle.Agent.SupportsImages;
        return EffectiveAgentRuntime.GetImageSupport(session.Provider)
            ?? session.Provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireImagesSupported(bool supported)
    {
        if (!supported)
            throw new KernelException(KernelErrorCode.AttachmentsUnsupported,
                "The provider session does not support image attachments.");
    }

    private IReadOnlyList<TurnImage>? ResolveTurnImages(IReadOnlyList<StoredImageAttachment> attachments)
    {
        if (attachments.Count == 0) return null;
        return attachments.Select(image => new TurnImage(image.ContentType, checked((int)image.Size), async ct =>
        {
            var media = await _store.OpenTrackMediaAsync(TrackId, image.Id, ct).ConfigureAwait(false);
            return media?.Content;
        })).ToArray();
    }
}
