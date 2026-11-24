using Perga.Contracts;

namespace Perga.Kernel.Ports;

public sealed record ImageMediaWrite(StoredImageAttachment Attachment, byte[] Bytes);
public sealed record TrackMedia(Stream Content, string ContentType);
