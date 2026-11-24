using System.Text;
using static Perga.MemoryKit.Tests.MemoryTestDirectory;

namespace Perga.MemoryKit.Tests;

public sealed class MemoryQueriesTests
{
    [Fact]
    public async Task List_and_exact_reads_order_Area_then_Global_by_pin_time_and_ordinal_key()
    {
        using var disk = new MemoryTestDirectory();
        var old = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc);
        var recent = old.AddDays(1);
        Write(Area, "z-pin", true, old);
        Write(Area, "a-pin", true, old);
        Write(Area, "later", true, recent);
        Write(Area, "same", false, recent);
        Write(Area, "b", false, old);
        Write(Global, "pinned", true, recent);
        Write(Global, "same", false, recent);
        Write(Global, "a", false, old);
        Write(new MemoryWriteScope(MemoryScopeKind.Area, "other"), "external", true, recent);

        string[] expected = ["area/later", "area/a-pin", "area/z-pin", "area/same", "area/b", "global/pinned", "global/same", "global/a"];
        var list = await disk.Engine.ListAsync(Mixed, Ct);
        Assert.Equal(expected, list.Select(meta => meta.Ref.ToString()));
        Assert.Equal(expected[5..], (await disk.Engine.ListAsync(GlobalOnly, Ct)).Select(meta => meta.Ref.ToString()));
        var reverseRequested = expected.Reverse().Select(MemoryRef.Parse).ToArray();
        var exact = await disk.Engine.ReadAsync(Mixed, reverseRequested, int.MaxValue, Ct);
        Assert.Equal(expected, exact.Entries.Select(entry => entry.Meta.Ref.ToString()));
        Assert.Empty(exact.NotFound);
        Assert.Equal(0, exact.OmittedCount);

        void Write(MemoryWriteScope scope, string key, bool pinned, DateTime mtime)
        {
            var path = disk.WriteHandwritten(scope, key + ".md",
                $"---\ntitle: note\nsummary: short\npinned: {pinned}\n---\nbody");
            File.SetLastWriteTimeUtc(path, mtime);
        }
    }

    [Fact]
    public async Task Exact_batches_omit_whole_files_by_UTF8_size_and_report_missing_independently()
    {
        using var disk = new MemoryTestDirectory();
        const string large = "界界";
        const string small = "界";
        disk.WriteHandwritten(Area, "large.md", large);
        disk.WriteHandwritten(Global, "small.md", small);
        disk.WriteHandwritten(new MemoryWriteScope(MemoryScopeKind.Area, "other"), "hidden.md", "outside");

        MemoryRef[] refs = [MemoryRef.Parse("global/small"), MemoryRef.Parse("global/missing"), MemoryRef.Parse("area/large"),
            MemoryRef.Parse("area/hidden"), MemoryRef.Parse("global/small")];
        var result = await disk.Engine.ReadAsync(Mixed, refs, 3, Ct);
        Assert.Equal(small, Assert.Single(result.Entries).Content);
        Assert.Equal(["area/hidden", "global/missing"], result.NotFound.Select(reference => reference.ToString()));
        Assert.Equal(1, result.OmittedCount);
        var none = await disk.Engine.ReadAsync(Mixed, refs, 0, Ct);
        Assert.Empty(none.Entries);
        Assert.Equal(2, none.OmittedCount);
        var global = await disk.Engine.ReadAsync(GlobalOnly, refs, 9, Ct);
        Assert.Equal(small, Assert.Single(global.Entries).Content);
        Assert.Equal(["area/hidden", "area/large", "global/missing"], global.NotFound.Select(reference => reference.ToString()));
    }

    [Fact]
    public async Task Recall_scores_all_keyword_fields_and_prioritizes_Area_with_canonical_display_order()
    {
        using var disk = new MemoryTestDirectory();
        await Put(Area, Request("pinned", "target") with { Pinned = true });
        await Put(Area, Request("target-key"));
        await Put(Area, Request("tag") with { Tags = ["target"] });
        await Put(Area, Request("title") with { Title = "target" });
        await Put(Area, Request("summary") with { Summary = "target" });
        await Put(Area, Request("shared", "Area guidance"));
        await Put(Global, Request("strong", "target") with { Title = "target", Summary = "target", Tags = ["target"] });
        await Put(Global, Request("shared", "target"));
        await Put(Global, Request("irrelevant", "unmatched"));

        var query = "TARGET target!";
        var all = await disk.Engine.RecallAsync(Mixed, query, 100, 100_000, Ct);
        Assert.Equal(6, all.Hits.Count);
        Assert.Equal("area/pinned", all.Hits[0].Entry.Meta.Ref.ToString());
        Assert.Equal("global/strong", all.Hits[^1].Entry.Meta.Ref.ToString());
        Assert.All(all.Hits.Take(all.Hits.Count - 1), hit => Assert.True(hit.Score > all.Hits[^1].Score));
        var scores = all.Hits.ToDictionary(hit => hit.Entry.Meta.Ref.Key, hit => hit.Score);
        Assert.True(scores["tag"] > scores["title"]);
        Assert.True(scores["title"] > scores["summary"]);
        Assert.True(scores["summary"] > scores["pinned"]);
        Assert.True(scores["target-key"] > scores["pinned"]);
        var recallOrder = (await disk.Engine.ListAsync(Mixed, Ct)).Select(meta => meta.Ref)
            .Where(reference => all.Hits.Any(hit => hit.Entry.Meta.Ref == reference));
        Assert.Equal(recallOrder, all.Hits.Select(hit => hit.Entry.Meta.Ref));

        var best = await disk.Engine.RecallAsync(Mixed, query, 1, int.MaxValue, Ct);
        var highest = Assert.Single(best.Hits);
        Assert.Equal("area/tag", highest.Entry.Meta.Ref.ToString());
        Assert.Equal(5, best.OmittedCount);
        var bounded = await disk.Engine.RecallAsync(Mixed, query, 10, Bytes(highest.Entry.Content), Ct);
        Assert.Equal(highest.Entry.Meta.Ref, Assert.Single(bounded.Hits).Entry.Meta.Ref);
        Assert.Equal(5, bounded.OmittedCount);
        var repeated = await disk.Engine.RecallAsync(Mixed, "target", 100, 100_000, Ct);
        Assert.Equal(all.Hits.Select(hit => (hit.Entry.Meta.Ref, hit.Score)), repeated.Hits.Select(hit => (hit.Entry.Meta.Ref, hit.Score)));

        var global = await disk.Engine.RecallAsync(GlobalOnly, query, 10, 100_000, Ct);
        Assert.Equal(["shared", "strong"], global.Hits.Select(hit => hit.Entry.Meta.Ref.Key).Order(StringComparer.Ordinal));
        Assert.Empty((await disk.Engine.RecallAsync(Mixed, "...", 10, 100, Ct)).Hits);

        async Task Put(MemoryWriteScope scope, MemoryPutRequest request) => await disk.Engine.PutAsync(scope, request, Ct);
    }

    [Fact]
    public async Task Auto_recall_returns_completed_empty_result_even_if_cancelled_or_storage_is_unavailable()
    {
        using var disk = new MemoryTestDirectory();
        File.WriteAllText(disk.Root, "This path cannot contain a memory scope.");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var result = disk.Engine.AutoRecallAsync(new MemoryReadScope("../unsafe"), "automation",
            new HashSet<MemoryRef> { MemoryRef.Parse("global/key") }, 10, 1000, cancelled.Token);
        Assert.True(result.IsCompletedSuccessfully);
        Assert.Empty((await result).Hits);
        Assert.Equal(0, (await result).OmittedCount);
    }

    [Fact]
    public async Task Snapshot_sees_memory_through_an_ordered_move_from_Global_to_Area()
    {
        using var disk = new MemoryTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var original = await disk.Engine.PutAsync(Global, Request("entry"), ct);
        Task<MemoryContextSnapshot> snapshot;
        using (var global = await MemoryScopeLocks.AcquireAsync(disk.GlobalPath, ct))
        {
            snapshot = disk.Engine.GetContextSnapshotAsync(Area.AreaId, new MemoryContextBudget(), ct);
            var area = MemoryScopeLocks.AcquireAsync(disk.AreaPath, ct);
            var canMoveWhileSnapshotWaits = area.IsCompletedSuccessfully;
            if (!canMoveWhileSnapshotWaits)
                global.Dispose(); // The snapshot holds Area, so let it finish before moving the file.

            (await area).Dispose();
            await disk.Engine.PutAsync(Area, Request("entry"), ct);
            if (canMoveWhileSnapshotWaits)
            {
                // Finish the move under the held Global lock, before the reader can scan Global.
                var directory = new MemoryDirectory(disk.Root).OpenScope(disk.GlobalPath);
                Assert.True(directory!.Delete("entry.md"));
            }
            else
            {
                Assert.True((await disk.Engine.DeleteAsync(Global, "entry", original.Meta.Etag, ct)).Deleted);
            }
        }

        Assert.Equal(1, (await snapshot.WaitAsync(ct)).SourceEntryCount);
        Assert.Equal("area/entry", Assert.Single(await disk.Engine.ListAsync(Mixed, ct)).Ref.ToString());
    }

    [Fact]
    public async Task Cancelling_a_snapshot_waiting_for_Global_releases_the_Area_lock()
    {
        using var disk = new MemoryTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var global = await MemoryScopeLocks.AcquireAsync(disk.GlobalPath, timeout.Token);
        var snapshot = disk.Engine.GetContextSnapshotAsync(Area.AreaId, new MemoryContextBudget(), cancellation.Token);
        Assert.False(snapshot.IsCompleted);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot.WaitAsync(timeout.Token));
        using var area = await MemoryScopeLocks.AcquireAsync(disk.AreaPath, timeout.Token);
    }

    [Theory]
    [InlineData(MemoryScopeKind.Area)]
    [InlineData(MemoryScopeKind.Global)]
    public async Task Snapshot_rolls_over_both_ways_and_hashes_exact_rendered_context(MemoryScopeKind borrowing)
    {
        using var disk = new MemoryTestDirectory(new MemoryEngineOptions(AreaPinnedBytes: 2048, GlobalPinnedBytes: 2048));
        var areaBytes = borrowing == MemoryScopeKind.Area ? 1600 : 400;
        var globalBytes = borrowing == MemoryScopeKind.Global ? 1600 : 400;
        await WritePin(Area, "shared", areaBytes);
        await WritePin(Global, "shared", globalBytes);
        await disk.Engine.PutAsync(Area, Request("topic") with { Summary = "Area form", Tags = ["First", "Other"] }, Ct);
        await disk.Engine.PutAsync(Global, Request("topic") with { Summary = "Global form" }, Ct);
        var snapshot = await disk.Engine.GetContextSnapshotAsync("work", new MemoryContextBudget(), Ct);

        Assert.Equal(1, snapshot.AreaPinnedCount);
        Assert.Equal(1, snapshot.GlobalPinnedCount);
        Assert.Equal(1, snapshot.AreaIndexCount);
        Assert.Equal(1, snapshot.GlobalIndexCount);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(snapshot.Content)), snapshot.Version);
        Assert.StartsWith("<memory_context>\nCurrent Area takes precedence over Global.\n", snapshot.Content);
        Assert.EndsWith("</memory_context>\n", snapshot.Content);
        Assert.Equal(areaBytes + globalBytes, Bytes(Section(snapshot.Content, "## Pinned\n", "## Topic Index\n")));
        Assert.Equal("area/topic — Area form [first, other]\nglobal/topic — Global form\n",
            Section(snapshot.Content, "## Topic Index\n", "</memory_context>\n"));
        Assert.True(snapshot.Content.IndexOf("### area/shared", StringComparison.Ordinal)
            < snapshot.Content.IndexOf("### global/shared", StringComparison.Ordinal));
        Assert.Equal(snapshot, await new MemoryEngine(disk.Root).GetContextSnapshotAsync("work", new MemoryContextBudget(), Ct));

        await disk.Engine.PutAsync(Area, Request("another") with { Summary = "New topic" }, Ct);
        Assert.NotEqual(snapshot.Version, (await disk.Engine.GetContextSnapshotAsync("work", new MemoryContextBudget(), Ct)).Version);

        async Task WritePin(MemoryWriteScope scope, string key, int byteCount)
        {
            var reference = new MemoryRef(scope.Kind, key);
            var body = new string('x', byteCount - Bytes($"### {reference} — Title\n界\n")) + "界\n";
            await disk.Engine.PutAsync(scope, Request(key, body) with { Pinned = true }, Ct);
        }
    }

    [Fact]
    public async Task Snapshot_caps_sections_by_bytes_preserves_Global_reserve_and_reports_only_rendered_counts()
    {
        using var disk = new MemoryTestDirectory(new MemoryEngineOptions(AreaPinnedBytes: 5000, GlobalPinnedBytes: 5000));
        var firstPin = await disk.Engine.PutAsync(Area, Request("area-pin", new string('界', 333)) with { Pinned = true }, Ct);
        var secondPin = await disk.Engine.PutAsync(Area, Request("area-extra", new string('界', 333)) with { Pinned = true }, Ct);
        await disk.Engine.PutAsync(Global, Request("global-pin", new string('界', 250)) with { Pinned = true }, Ct);
        File.SetLastWriteTimeUtc(Path.Combine(disk.AreaPath, secondPin.Meta.Ref.Key + ".md"), new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(disk.AreaPath, firstPin.Meta.Ref.Key + ".md"), new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc));

        var summary = new string('界', 140);
        await disk.Engine.PutAsync(Area, Request("topic-a") with { Summary = summary }, Ct);
        await disk.Engine.PutAsync(Area, Request("topic-b") with { Summary = summary }, Ct);
        await disk.Engine.PutAsync(Global, Request("topic-g") with { Summary = summary }, Ct);
        var snapshot = await disk.Engine.GetContextSnapshotAsync("work", new MemoryContextBudget(int.MaxValue, int.MaxValue), Ct);
        Assert.Equal(1, snapshot.AreaPinnedCount);
        Assert.Equal(1, snapshot.GlobalPinnedCount);
        Assert.Equal(2, snapshot.AreaIndexCount);
        Assert.Equal(0, snapshot.GlobalIndexCount);
        Assert.Contains("### area/area-pin", snapshot.Content);
        Assert.DoesNotContain("area-extra", snapshot.Content);
        Assert.True(Bytes(Section(snapshot.Content, "## Pinned\n", "## Topic Index\n")) <= 2048);
        Assert.True(Bytes(Section(snapshot.Content, "## Topic Index\n", "</memory_context>\n")) <= 1024);
        var empty = await disk.Engine.GetContextSnapshotAsync("work", new MemoryContextBudget(0, 0), Ct);
        Assert.Equal("<memory_context>\nCurrent Area takes precedence over Global.\n</memory_context>\n", empty.Content);
        Assert.Equal((0, 0, 0, 0), (empty.AreaPinnedCount, empty.GlobalPinnedCount, empty.AreaIndexCount, empty.GlobalIndexCount));

        var indexOnly = await disk.Engine.GetContextSnapshotAsync("work", new MemoryContextBudget(PinnedBytes: 0), Ct);
        Assert.DoesNotContain("## Pinned\n", indexOnly.Content);
        Assert.Contains("## Topic Index\n", indexOnly.Content);
        var pinnedOnly = await disk.Engine.GetContextSnapshotAsync("work", new MemoryContextBudget(TopicIndexBytes: 0), Ct);
        Assert.Contains("## Pinned\n", pinnedOnly.Content);
        Assert.DoesNotContain("## Topic Index\n", pinnedOnly.Content);
    }

    private static string Section(string content, string start, string end)
    {
        var offset = content.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return content[offset..content.IndexOf(end, offset, StringComparison.Ordinal)];
    }
}
