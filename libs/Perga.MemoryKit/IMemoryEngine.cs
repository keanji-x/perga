namespace Perga.MemoryKit;

public interface IMemoryEngine
{
    Task<IReadOnlyList<MemoryEntryMeta>> ListAsync(MemoryReadScope scope, CancellationToken ct);

    /// <summary>Reads distinct explicit references, charging the UTF-8 bytes of their complete files.</summary>
    Task<MemoryReadResult> ReadAsync(MemoryReadScope scope, IReadOnlyList<MemoryRef> refs, int maxBytes, CancellationToken ct);

    /// <summary>Selects keyword matches by relevance and presents the selected files in canonical order.</summary>
    /// <remarks>A current Area entry shadows the same key in Global. Explicit reads can inspect either.</remarks>
    Task<MemoryRecallResult> RecallAsync(MemoryReadScope scope, string query, int maxItems, int maxBytes, CancellationToken ct);

    /// <summary>Recalls entries outside the supplied references, applying the budget after exclusions.</summary>
    Task<MemoryRecallResult> RecallAsync(MemoryReadScope scope, string query, int maxItems, int maxBytes,
        IReadOnlySet<MemoryRef>? exclude, CancellationToken ct);

    /// <summary>Reserved for automatic recall. Stage 1 always returns an empty completed result.</summary>
    Task<MemoryRecallResult> AutoRecallAsync(MemoryReadScope scope, string userPrompt, IReadOnlySet<MemoryRef> exclude,
        int maxItems, int maxBytes, CancellationToken ct);

    Task<MemoryContextSnapshot> GetContextSnapshotAsync(string? areaId, MemoryContextBudget budget, CancellationToken ct);
    Task<MemoryPutResult> PutAsync(MemoryWriteScope scope, MemoryPutRequest request, CancellationToken ct);
    Task<MemoryDeleteResult> DeleteAsync(MemoryWriteScope scope, string key, string? expectedEtag, CancellationToken ct);
    Task DeleteAreaDirectoryAsync(string areaId, CancellationToken ct);
    Task CleanupOrphanAreaDirectoriesAsync(IReadOnlySet<string> validUserAreaIds, CancellationToken ct);
}
