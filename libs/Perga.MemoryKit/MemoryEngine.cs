using System.Text.RegularExpressions;

namespace Perga.MemoryKit;

/// <summary>A Markdown-backed memory engine with no derived state on disk.</summary>
public sealed partial class MemoryEngine : IMemoryEngine
{
    private static readonly Task<MemoryRecallResult> EmptyRecall = Task.FromResult(new MemoryRecallResult([], 0));
    private static readonly MemoryWriteScope Global = new(MemoryScopeKind.Global);
    private readonly MemoryDirectory _directory;
    private readonly MemoryEngineOptions _options;

    public MemoryEngine(string root, MemoryEngineOptions? options = null)
    {
        _directory = new MemoryDirectory(root);
        _options = options ?? new MemoryEngineOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(_options.AreaPinnedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.GlobalPinnedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxEntriesPerScope);
    }

    public async Task<IReadOnlyList<MemoryEntryMeta>> ListAsync(MemoryReadScope scope, CancellationToken ct) =>
        (await LoadAsync(scope, refs: null, ct)).Files.Select(file => file.Meta).ToArray();

    public async Task<MemoryReadResult> ReadAsync(
        MemoryReadScope scope, IReadOnlyList<MemoryRef> refs, int maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var requested = refs.Select(reference => new MemoryRef(reference.Kind, reference.Key)).Distinct().ToArray();
        var files = (await LoadAsync(scope, requested, ct)).Files;
        var found = files.Select(file => file.Meta.Ref).ToHashSet();
        var notFound = requested.Where(reference => !found.Contains(reference))
            .OrderBy(reference => reference.Kind)
            .ThenBy(reference => reference.Key, StringComparer.Ordinal)
            .ToArray();
        var entries = Fit(files, file => file.ByteCount, int.MaxValue, maxBytes, out var omittedCount);
        return new MemoryReadResult(entries.Select(file => file.Entry).ToArray(), notFound, omittedCount);
    }

    public Task<MemoryRecallResult> RecallAsync(
        MemoryReadScope scope, string query, int maxItems, int maxBytes, CancellationToken ct) =>
        RecallAsync(scope, query, maxItems, maxBytes, exclude: null, ct);

    public async Task<MemoryRecallResult> RecallAsync(
        MemoryReadScope scope, string query, int maxItems, int maxBytes, IReadOnlySet<MemoryRef>? exclude, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(maxItems);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ct.ThrowIfCancellationRequested();
        var terms = QueryTerms().Matches(query).Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length == 0)
            return await EmptyRecall;

        var files = (await LoadAsync(scope, refs: null, ct)).Files;
        var areaKeys = files.Where(file => file.Meta.Ref.Kind == MemoryScopeKind.Area)
            .Select(file => file.Meta.Ref.Key).ToHashSet(StringComparer.Ordinal);
        var matches = new List<(MemoryFile File, long Score)>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var reference = file.Meta.Ref;
            if (exclude?.Contains(reference) == true ||
                reference.Kind == MemoryScopeKind.Global && areaKeys.Contains(reference.Key))
                continue;

            var score = Score(file, terms);
            if (score > 0)
                matches.Add((file, score));
        }

        // Stable sorting preserves canonical order among equally relevant entries.
        // Select by relevance within the budget, then present in canonical order.
        var ranked = matches.OrderByDescending(hit => hit.Score);
        var selected = Fit(ranked, hit => hit.File.ByteCount, maxItems, maxBytes, out var omittedCount);
        var scores = selected.ToDictionary(hit => hit.File.Meta.Ref, hit => hit.Score);
        var hits = files.Where(file => scores.ContainsKey(file.Meta.Ref))
            .Select(file => new MemoryRecallHit(file.Entry, scores[file.Meta.Ref])).ToArray();
        return new MemoryRecallResult(hits, omittedCount);
    }

    public Task<MemoryRecallResult> AutoRecallAsync(MemoryReadScope scope, string userPrompt,
        IReadOnlySet<MemoryRef> exclude, int maxItems, int maxBytes, CancellationToken ct) => EmptyRecall;

    public async Task<MemoryContextSnapshot> GetContextSnapshotAsync(
        string? areaId, MemoryContextBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var source = await LoadAsync(new MemoryReadScope(areaId), refs: null, ct);
        return MemoryContext.Render(source.Files, source.SourceEntryCount, budget);
    }

    public async Task<MemoryPutResult> PutAsync(MemoryWriteScope scope, MemoryPutRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        var reference = new MemoryRef(scope.Kind, request.Key);
        var directory = _directory.ScopePath(scope);
        var name = MemoryDirectory.EntryName(reference.Key);
        ct.ThrowIfCancellationRequested();
        var (bytes, file) = MemoryMarkdown.Prepare(reference, request);

        using var lease = await MemoryScopeLocks.AcquireAsync(directory, ct);
        var existingScope = _directory.OpenScope(directory);
        await CheckPutAsync(existingScope, name, file, request.ExpectedEtag, ct);

        var newScope = existingScope is null ? _directory.OpenScope(directory, create: true) : null;
        var scopeDirectory = existingScope ?? newScope ?? throw new DirectoryNotFoundException(directory);
        if (newScope is not null)
            await CheckPutAsync(newScope, name, file, request.ExpectedEtag, ct);
        var updatedAt = await scopeDirectory.WriteFileAsync(name, bytes, ct);
        return new MemoryPutResult(file.Meta with { UpdatedAtMs = new DateTimeOffset(updatedAt).ToUnixTimeMilliseconds() });
    }

    public async Task<MemoryDeleteResult> DeleteAsync(
        MemoryWriteScope scope, string key, string? expectedEtag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var reference = new MemoryRef(scope.Kind, key);
        var directory = _directory.ScopePath(scope);
        var name = MemoryDirectory.EntryName(key);
        using var lease = await MemoryScopeLocks.AcquireAsync(directory, ct);
        var scopeDirectory = _directory.OpenScope(directory);
        if (scopeDirectory is null || !await RequireCurrentEtagAsync(scopeDirectory, name, reference, expectedEtag, ct))
            return new MemoryDeleteResult(false);

        ct.ThrowIfCancellationRequested();
        return new MemoryDeleteResult(scopeDirectory.Delete(name));
    }

    public async Task DeleteAreaDirectoryAsync(string areaId, CancellationToken ct)
    {
        var directory = _directory.ScopePath(new MemoryWriteScope(MemoryScopeKind.Area, areaId));
        using var lease = await MemoryScopeLocks.AcquireAsync(directory, ct);
        ct.ThrowIfCancellationRequested();
        MemoryDirectory.TryCleanup(() => _directory.DeleteAreaDirectory(directory, ct));
    }

    public async Task CleanupOrphanAreaDirectoriesAsync(IReadOnlySet<string> validUserAreaIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(validUserAreaIds);
        var cutoffUtc = DateTime.UtcNow.AddMinutes(-5);
        var globalDirectory = _directory.ScopePath(Global);
        using (await MemoryScopeLocks.AcquireAsync(globalDirectory, ct))
            MemoryDirectory.TryCleanup(() => _directory.CleanupStaleTemporaryFiles(globalDirectory, cutoffUtc, ct));

        var validIds = validUserAreaIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in _directory.EnumerateAreaDirectories())
        {
            using var lease = await MemoryScopeLocks.AcquireAsync(directory, ct);
            ct.ThrowIfCancellationRequested();
            MemoryDirectory.TryCleanup(() =>
            {
                if (validIds.Contains(Path.GetFileName(directory)))
                    _directory.CleanupStaleTemporaryFiles(directory, cutoffUtc, ct);
                else
                    _directory.DeleteAreaDirectory(directory, ct);
            });
        }
    }

    private async Task CheckPutAsync(MemoryDirectory.ScopeDirectory? scope, string name, MemoryFile proposed,
        string? expectedEtag, CancellationToken ct)
    {
        if (scope is not null && !await RequireCurrentEtagAsync(scope, name, proposed.Meta.Ref, expectedEtag, ct))
        {
            var count = 0;
            foreach (var _ in EnumerateEntryNames(scope))
            {
                ct.ThrowIfCancellationRequested();
                if (++count >= _options.MaxEntriesPerScope)
                    throw new MemoryValidationException(
                        $"This memory scope has reached the {_options.MaxEntriesPerScope}-entry limit. Merge or delete existing entries before adding new ones.");
            }
        }
        if (proposed.Meta.Pinned)
            await CheckPinnedBudgetAsync(scope, proposed, ct);
    }

    private async Task CheckPinnedBudgetAsync(MemoryDirectory.ScopeDirectory? scope, MemoryFile proposed, CancellationToken ct)
    {
        var reference = proposed.Meta.Ref;
        var limit = reference.Kind == MemoryScopeKind.Area ? _options.AreaPinnedBytes : _options.GlobalPinnedBytes;
        var current = scope is null ? [] : (await LoadScopeAsync(scope, reference.Kind, refs: null, ct, requireReadableFiles: true)).Files;
        var breakdown = Order(current.Where(file => file.Meta.Pinned))
            .Select(file => new MemoryPinnedEntryBytes(file.Meta.Ref.Key, PinnedByteCount(file))).ToArray();
        var proposedBytes = PinnedByteCount(proposed);
        var projected = breakdown.Where(item => item.Key != reference.Key).Sum(item => (long)item.Bytes) + proposedBytes;
        if (projected <= limit)
            return;

        var details = string.Join(", ", breakdown.Select(item => $"{item.Key}={item.Bytes} B"));
        throw new MemoryValidationException(
            $"The {reference.Kind} pinned budget is {limit} B. '{reference.Key}' needs {proposedBytes} B, " +
            $"bringing the scope to {projected} B. Currently pinned: [{details}].",
            new MemoryPinnedBudget(reference.Kind, limit, breakdown, reference.Key, proposedBytes, projected));
    }

    private static int PinnedByteCount(MemoryFile file) => MemoryMarkdown.Utf8.GetByteCount(MemoryContext.RenderPinned(file));

    private static async Task<bool> RequireCurrentEtagAsync(
        MemoryDirectory.ScopeDirectory scope, string name, MemoryRef reference, string? expectedEtag, CancellationToken ct)
    {
        if (await scope.ReadFileAsync(name, rejectUnsafe: true, ct) is not { } current)
            return false;
        var etag = MemoryMarkdown.Hash(current.Bytes);
        if (!string.Equals(etag, expectedEtag, StringComparison.Ordinal))
            throw new MemoryConflictException(reference, etag);
        return true;
    }

    private async Task<ScopeLoad> LoadAsync(
        MemoryReadScope scope, IReadOnlyList<MemoryRef>? refs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var scopes = scope.AreaId is null ? new[] { Global } : [new MemoryWriteScope(MemoryScopeKind.Area, scope.AreaId), Global];
        var reads = scopes.Select(candidate => (
                candidate.Kind,
                Directory: _directory.ScopePath(candidate),
                Requested: refs?.Where(reference => reference.Kind == candidate.Kind).ToArray()))
            .Where(read => read.Requested is not { Length: 0 })
            .ToArray();
        using var lease = await MemoryScopeLocks.AcquireAllAsync(reads.Select(read => read.Directory), ct);
        var files = new List<MemoryFile>();
        var sourceEntryCount = 0;
        foreach (var read in reads)
        {
            var scopeDirectory = _directory.OpenScope(read.Directory);
            if (scopeDirectory is null)
                continue;
            var loaded = await LoadScopeAsync(scopeDirectory, read.Kind, read.Requested, ct);
            files.AddRange(loaded.Files);
            sourceEntryCount += loaded.SourceEntryCount;
        }

        return new(Order(files).ToArray(), sourceEntryCount);
    }

    private static async Task<ScopeLoad> LoadScopeAsync(
        MemoryDirectory.ScopeDirectory scope, MemoryScopeKind kind, IReadOnlyList<MemoryRef>? refs, CancellationToken ct,
        bool requireReadableFiles = false)
    {
        var files = new List<MemoryFile>();
        var sourceEntryCount = 0;
        var names = refs is null
            ? EnumerateEntryNames(scope)
            : refs.Select(reference => MemoryDirectory.EntryName(reference.Key));
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            sourceEntryCount++;
            var key = Path.GetFileNameWithoutExtension(name);
            if (!MemoryRef.IsValidKey(key))
                continue;

            try
            {
                if (await scope.ReadFileAsync(MemoryDirectory.EntryName(key), rejectUnsafe: false, ct) is { } raw
                    && MemoryMarkdown.Read(new(kind, key), raw.Bytes, raw.UpdatedAtUtc) is { } file)
                    files.Add(file);
            }
            catch (Exception ex) when (!requireReadableFiles && ex is (IOException or UnauthorizedAccessException))
            {
                // The entry still counts as a source even if its contents cannot be read.
            }
        }
        return new(files, sourceEntryCount);
    }

    private static IEnumerable<string> EnumerateEntryNames(MemoryDirectory.ScopeDirectory scope) =>
        scope.EnumerateFileNames().Where(name => !name.StartsWith('.') && Path.GetExtension(name) == ".md");

    private sealed record ScopeLoad(IReadOnlyList<MemoryFile> Files, int SourceEntryCount);

    private static IOrderedEnumerable<MemoryFile> Order(IEnumerable<MemoryFile> files) => files
        .OrderBy(file => file.Meta.Ref.Kind)
        .ThenByDescending(file => file.Meta.Pinned)
        .ThenByDescending(file => file.Meta.UpdatedAtMs)
        .ThenBy(file => file.Meta.Ref.Key, StringComparer.Ordinal);

    private static List<T> Fit<T>(IEnumerable<T> candidates, Func<T, int> size, int maxItems, int maxBytes, out int omitted)
    {
        omitted = 0;
        var selected = new List<T>();
        foreach (var candidate in candidates)
        {
            var bytes = size(candidate);
            if (selected.Count == maxItems || bytes > maxBytes)
            {
                omitted++;
                continue;
            }

            selected.Add(candidate);
            maxBytes -= bytes;
        }

        return selected;
    }

    private static long Score(MemoryFile file, string[] terms)
    {
        const int keyWeight = 8;
        const int tagWeight = 16;
        const int titleWeight = 8;
        const int summaryWeight = 4;
        const int bodyWeight = 1;
        long score = 0;
        foreach (var term in terms)
        {
            const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
            if (file.Meta.Ref.Key.Contains(term, comparison))
                score += keyWeight;
            if (file.Meta.Tags.Any(tag => tag.Contains(term, comparison)))
                score += tagWeight;
            if (file.Meta.Title.Contains(term, comparison))
                score += titleWeight;
            if (file.Meta.Summary.Contains(term, comparison))
                score += summaryWeight;
            if (file.Body.Contains(term, comparison))
                score += bodyWeight;
        }

        const int maxWeight = keyWeight + tagWeight + titleWeight + summaryWeight + bodyWeight;
        if (score > 0 && file.Meta.Ref.Kind == MemoryScopeKind.Area)
            score += (long)terms.Length * maxWeight;
        return score;
    }

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex QueryTerms();
}
