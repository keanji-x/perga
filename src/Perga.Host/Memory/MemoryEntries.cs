using Perga.MemoryKit;

namespace Perga.Host.Memory;

public sealed record MemoryEntryDto(string Ref, string Key, string Title, string Summary, bool Pinned,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Sources, string Body, int SizeBytes, long UpdatedAtMs, string Etag);

public sealed record MemoryEntryMetaDto(string Ref, string Key, string Title, string Summary, bool Pinned,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Sources, long UpdatedAtMs, string Etag);

internal static class MemoryEntries
{
    internal static MemoryEntryDto ToDto(this MemoryEntry entry)
    {
        var meta = entry.Meta;
        return new(meta.Ref.ToString(), meta.Ref.Key, meta.Title, meta.Summary, meta.Pinned,
            meta.Tags, meta.Sources, entry.Body, entry.SizeBytes, meta.UpdatedAtMs, meta.Etag);
    }

    internal static MemoryEntryMetaDto ToDto(this MemoryEntryMeta meta) =>
        new(meta.Ref.ToString(), meta.Ref.Key, meta.Title, meta.Summary, meta.Pinned,
            meta.Tags, meta.Sources, meta.UpdatedAtMs, meta.Etag);
}
