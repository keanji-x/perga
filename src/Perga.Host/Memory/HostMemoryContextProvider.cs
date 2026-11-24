using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Perga.Host.Serialization;
using Perga.Kernel.Ports;
using Perga.MemoryKit;

namespace Perga.Host.Memory;

internal sealed partial class HostMemoryContextProvider(
    IMemoryEngine engine, MemoryEngineOptions options, ILogger<HostMemoryContextProvider> logger)
    : IMemoryContextProvider
{
    private const int ExplicitBudget = 8 * 1024;
    private const int MaxMissingRefs = 8;
    private const int RecallBudget = 6 * 1024;
    private const int RecallItemLimit = 32;

    public async Task<JsonElement?> GetMemoryTemplateDataAsync(string? areaId, CancellationToken ct)
    {
        var scope = new MemoryReadScope(areaId);
        var kind = areaId is null ? MemoryScopeKind.Global : MemoryScopeKind.Area;
        var metadata = await engine.ListAsync(scope, ct).ConfigureAwait(false);
        var refs = metadata.Where(entry => entry.Ref.Kind == kind).Select(entry => entry.Ref).ToArray();
        var result = await engine.ReadAsync(scope, refs, int.MaxValue, ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            Scope = areaId is null ? "global" : "area",
            AreaId = areaId,
            PinnedBudgetBytes = areaId is null ? options.GlobalPinnedBytes : options.AreaPinnedBytes,
            PinnedUsedBytes = result.Entries.Where(entry => entry.Meta.Pinned).Sum(entry => (long)entry.PinnedContextBytes),
            GlobalEntryCount = metadata.Count(entry => entry.Ref.Kind == MemoryScopeKind.Global),
            Entries = result.Entries.Select(MemoryEntries.ToDto).ToArray()
        }, PergaJsonDefaults.ApiOptions);
    }

    public Task<MemoryTurnSnapshot?> GetContextSnapshotAsync(string? areaId, CancellationToken ct) =>
        ReadOptionalAsync<MemoryTurnSnapshot?>(async () =>
        {
            var snapshot = await engine.GetContextSnapshotAsync(areaId, new(), ct).ConfigureAwait(false);
            var entryCount = snapshot.AreaPinnedCount + snapshot.GlobalPinnedCount + snapshot.AreaIndexCount + snapshot.GlobalIndexCount;
            if (entryCount > 0)
                return new(snapshot.Content, snapshot.Version, entryCount);
            return snapshot.SourceEntryCount == 0 && snapshot.UnreadableEntryCount == 0 ? new("", "", 0) : null;
        }, null);

    public Task<string> AutoRecallAsync(string? areaId, string ownerContent, CancellationToken ct) =>
        ReadOptionalAsync(async () =>
        {
            var recall = await engine.AutoRecallAsync(new(areaId), ownerContent, new HashSet<MemoryRef>(),
                RecallItemLimit, RecallBudget, ct).ConfigureAwait(false);
            return Render("recalled_memory", recall.Hits.Select(hit => hit.Entry));
        }, "");

    public Task<string> HydrateMentionsAsync(string? areaId, string ownerContent, CancellationToken ct) =>
        ReadOptionalAsync(() => HydrateCoreAsync(new(areaId), ownerContent, ct), "");

    public Task DeleteAreaDirectoryAsync(string areaId, CancellationToken ct) =>
        ReadOptionalAsync(async () =>
        {
            await engine.DeleteAreaDirectoryAsync(areaId, ct).ConfigureAwait(false);
            return true;
        }, false);

    public Task CleanupOrphanAreaDirectoriesAsync(IReadOnlySet<string> validUserAreaIds, CancellationToken ct) =>
        ReadOptionalAsync(async () =>
        {
            await engine.CleanupOrphanAreaDirectoriesAsync(validUserAreaIds, ct).ConfigureAwait(false);
            return true;
        }, false);

    private async Task<string> HydrateCoreAsync(MemoryReadScope scope, string ownerContent, CancellationToken ct)
    {
        var matches = Mentions().Matches(ownerContent);
        if (matches.Count == 0) return "";

        var exactRefs = new HashSet<MemoryRef>();
        var scopes = new HashSet<MemoryScopeKind>();
        var bare = false;
        foreach (Match match in matches)
        {
            var address = match.Groups["address"].Value;
            if (MemoryRef.TryParse(address, out var reference))
                exactRefs.Add(reference);
            else if (address is "area" or "global")
                scopes.Add(address == "area" ? MemoryScopeKind.Area : MemoryScopeKind.Global);
            else
                bare = true;
        }

        var entries = new List<MemoryEntry>();
        var remainingBytes = ExplicitBudget;
        var omittedCount = 0;
        IReadOnlyList<MemoryRef> notFound = [];
        if (exactRefs.Count > 0)
        {
            var exact = await ReadIntoContextAsync(exactRefs.ToArray()).ConfigureAwait(false);
            notFound = exact.NotFound;
        }

        var recalledExclusions = new HashSet<MemoryRef>(exactRefs);
        if (scopes.Count > 0)
        {
            var available = await engine.ListAsync(scope, ct).ConfigureAwait(false);
            var scopeRefs = available.Select(entry => entry.Ref)
                .Where(reference => scopes.Contains(reference.Kind) && !exactRefs.Contains(reference)).ToArray();
            if (scopeRefs.Length > 0)
            {
                await ReadIntoContextAsync(scopeRefs).ConfigureAwait(false);
                recalledExclusions.UnionWith(scopeRefs);
            }
        }
        if (bare)
        {
            var query = Mentions().Replace(ownerContent, " ");
            var recall = await engine.RecallAsync(scope, query, RecallItemLimit,
                Math.Min(remainingBytes, RecallBudget), recalledExclusions, ct).ConfigureAwait(false);
            entries.AddRange(recall.Hits.Select(hit => hit.Entry));
            omittedCount += recall.OmittedCount;
        }

        return RenderExplicit(entries, notFound, omittedCount);

        async Task<MemoryReadResult> ReadIntoContextAsync(IReadOnlyList<MemoryRef> refs)
        {
            var result = await engine.ReadAsync(scope, refs, remainingBytes, ct).ConfigureAwait(false);
            entries.AddRange(result.Entries);
            remainingBytes -= result.Entries.Sum(entry => entry.SizeBytes);
            omittedCount += result.OmittedCount;
            return result;
        }
    }

    private static string RenderExplicit(IEnumerable<MemoryEntry> entries, IReadOnlyList<MemoryRef> notFound, int omittedCount)
    {
        const string opening = "<explicit_memory>\n";
        const string closing = "</explicit_memory>\n";
        var missing = notFound.Count > 0
            ? $"Not found: {string.Join(", ", notFound.Take(MaxMissingRefs))}" : "";
        if (notFound.Count > MaxMissingRefs)
            missing += $" (+{notFound.Count - MaxMissingRefs} more)";
        if (missing.Length > 0)
            missing += "\n";

        var blocks = entries.Select(entry =>
        {
            var text = RenderEntry(entry);
            return (entry.Meta, Text: text, Bytes: Encoding.UTF8.GetByteCount(text));
        }).ToArray();
        var remaining = ExplicitBudget - Encoding.UTF8.GetByteCount(opening + missing + closing);
        if (omittedCount > 0 || blocks.Sum(block => block.Bytes) > remaining)
            remaining -= Encoding.UTF8.GetByteCount(RenderOmitted(omittedCount + blocks.Length));

        var selected = new List<(MemoryEntryMeta Meta, string Text)>();
        foreach (var block in blocks)
        {
            if (block.Bytes > remaining)
            {
                omittedCount++;
                continue;
            }
            selected.Add((block.Meta, block.Text));
            remaining -= block.Bytes;
        }

        var output = new StringBuilder(opening);
        foreach (var block in selected.OrderBy(block => block.Meta.Ref.Kind)
            .ThenByDescending(block => block.Meta.Pinned).ThenByDescending(block => block.Meta.UpdatedAtMs)
            .ThenBy(block => block.Meta.Ref.Key, StringComparer.Ordinal))
            output.Append(block.Text);
        output.Append(missing);
        if (omittedCount > 0)
            output.Append(RenderOmitted(omittedCount));
        return output.Length == opening.Length ? "" : output.Append(closing).ToString();
    }

    private static string RenderEntry(MemoryEntry entry) => $"### {entry.Meta.Ref} — {entry.Meta.Title}\n{entry.Body}\n";

    private static string RenderOmitted(int count)
    {
        var noun = count == 1 ? "entry" : "entries";
        return $"Note: {count} additional {noun} omitted due to budget; call perga_memory list or read for details.\n";
    }

    private static string Render(string tag, IEnumerable<MemoryEntry> entries)
    {
        var output = new StringBuilder();
        foreach (var entry in entries)
            output.Append(RenderEntry(entry));
        return output.Length == 0 ? "" : $"<{tag}>\n{output}</{tag}>\n";
    }

    private async Task<T> ReadOptionalAsync<T>(Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MemoryValidationException)
        {
            logger.LogWarning(ex, "Could not access the memory directory.");
            return fallback;
        }
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}_@])@(?:\x60memory:(?<address>(?:area|global)(?:/[a-z0-9][a-z0-9_-]{0,63})?)\x60|memory(?![\p{L}\p{N}_:/-]))",
        RegexOptions.CultureInvariant)]
    private static partial Regex Mentions();
}
