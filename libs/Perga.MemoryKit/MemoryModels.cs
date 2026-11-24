namespace Perga.MemoryKit;

/// <summary>Global memory and, when supplied, the current area's memory.</summary>
public sealed record MemoryReadScope(string? AreaId = null);

/// <summary>The single scope in which a write is allowed.</summary>
public sealed record MemoryWriteScope(MemoryScopeKind Kind, string? AreaId = null);

public sealed record MemoryEntryMeta(
    MemoryRef Ref, string Title, string Summary, bool Pinned, IReadOnlyList<string> Tags,
    IReadOnlyList<string> Sources, long UpdatedAtMs, string Etag);

/// <summary>Content is the complete UTF-8 Markdown file, including its frontmatter if present.</summary>
public sealed record MemoryEntry(MemoryEntryMeta Meta, string Content)
{
    public string Body
    {
        get
        {
            _ = MemoryFrontmatter.Read(Content, out var body);
            return body;
        }
    }

    public int SizeBytes => MemoryMarkdown.Utf8.GetByteCount(Content);
    public int PinnedContextBytes => MemoryMarkdown.Utf8.GetByteCount(MemoryContext.RenderPinned(Meta, Body));
}

public sealed record MemoryReadResult(
    IReadOnlyList<MemoryEntry> Entries, IReadOnlyList<MemoryRef> NotFound, int OmittedCount);

public sealed record MemoryRecallHit(MemoryEntry Entry, long Score);

public sealed record MemoryRecallResult(IReadOnlyList<MemoryRecallHit> Hits, int OmittedCount);

/// <summary>UTF-8 byte limits for the section payloads within the context envelope.</summary>
public sealed record MemoryContextBudget(
    int PinnedBytes = MemoryContext.MaxPinnedBytes, int TopicIndexBytes = MemoryContext.MaxTopicIndexBytes);

public sealed record MemoryContextSnapshot(
    string Content, string Version, int AreaPinnedCount, int GlobalPinnedCount, int AreaIndexCount, int GlobalIndexCount,
    int SourceEntryCount = 0, int UnreadableEntryCount = 0);

public sealed record MemoryPutRequest(
    string Key, string Title, string Summary, string Body, bool Pinned = false,
    IReadOnlyList<string>? Tags = null, IReadOnlyList<string>? Sources = null, string? ExpectedEtag = null);

public sealed record MemoryPutResult(MemoryEntryMeta Meta);

public sealed record MemoryDeleteResult(bool Deleted);

/// <summary>Per-scope limits for entry count and rendered pinned entries in UTF-8 bytes.</summary>
public sealed record MemoryEngineOptions(
    int AreaPinnedBytes = MemoryContext.AreaPinnedBytes, int GlobalPinnedBytes = MemoryContext.GlobalPinnedBytes)
{
    public int MaxEntriesPerScope { get; init; } = 2_000;
}

public sealed record MemoryPinnedEntryBytes(string Key, int Bytes);

public sealed record MemoryPinnedBudget(
    MemoryScopeKind Kind, int LimitBytes, IReadOnlyList<MemoryPinnedEntryBytes> CurrentPinned,
    string RequestedKey, int RequestedBytes, long ProjectedBytes);

public sealed class MemoryValidationException(string message, MemoryPinnedBudget? pinnedBudget = null) : Exception(message)
{
    public MemoryPinnedBudget? PinnedBudget { get; } = pinnedBudget;
}

public sealed class MemoryConflictException(MemoryRef reference, string currentEtag)
    : Exception($"Memory entry '{reference}' has ETag '{currentEtag}'. Supply that ETag to modify the entry.")
{
    public MemoryRef Ref { get; } = reference;
    public string CurrentEtag { get; } = currentEtag;
}
