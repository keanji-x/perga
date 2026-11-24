using System.Text;
using static Perga.MemoryKit.Tests.MemoryTestDirectory;

namespace Perga.MemoryKit.Tests;

public sealed class MemoryMarkdownAndWritesTests
{
    [Fact]
    public async Task Entry_limit_allows_updates_and_deletes_at_and_above_capacity()
    {
        using var disk = new MemoryTestDirectory();
        for (var i = 0; i < 1_998; i++)
            disk.WriteHandwritten(Global, $"imported-{i}.md", "A note added outside the engine.");
        var first = await disk.Engine.PutAsync(Global, Request("first"), Ct);
        await disk.Engine.PutAsync(Global, Request("second"), Ct);
        await disk.Engine.PutAsync(Area, Request("independent"), Ct);

        var added = Request("new");
        var full = await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.PutAsync(Global, added, Ct));
        Assert.Equal("This memory scope has reached the 2000-entry limit. Merge or delete existing entries before adding new ones.", full.Message);
        var updated = await disk.Engine.PutAsync(Global, Request("first", "Updated at capacity.")
            with { ExpectedEtag = first.Meta.Etag }, Ct);
        Assert.NotEqual(first.Meta.Etag, updated.Meta.Etag);

        disk.WriteHandwritten(Global, "manual.md", "A note added outside the engine.");
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.PutAsync(Global, added, Ct));
        updated = await disk.Engine.PutAsync(Global, Request("first", "Updated above capacity.")
            with { ExpectedEtag = updated.Meta.Etag }, Ct);
        Assert.True((await disk.Engine.DeleteAsync(Global, "first", updated.Meta.Etag, Ct)).Deleted);
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.PutAsync(Global, added, Ct));

        var manual = Assert.Single(await disk.Engine.ListAsync(GlobalOnly, Ct), entry => entry.Ref.Key == "manual");
        Assert.True((await disk.Engine.DeleteAsync(Global, "manual", manual.Etag, Ct)).Deleted);
        var created = await disk.Engine.PutAsync(Global, added, Ct);
        Assert.Equal("global/new", created.Meta.Ref.ToString());
    }

    [Fact]
    public async Task FallbackSummarySkipsLeadingHtmlComments()
    {
        using var disk = new MemoryTestDirectory();
        const string body = "<!-- one line -->\n<!-- retained metadata\n# Hidden heading\n-->\nVisible summary.\n\n# Entry title\n";
        disk.WriteHandwritten(Global, "commented.md", body);

        var entry = Assert.Single((await disk.Engine.ReadAsync(GlobalOnly,
            [MemoryRef.Parse("global/commented")], int.MaxValue, Ct)).Entries);
        Assert.Equal("Entry title", entry.Meta.Title);
        Assert.Equal("Visible summary.", entry.Meta.Summary);
        Assert.Equal(body, entry.Body);
    }

    [Theory]
    [InlineData("Visible <!-- internal draft note --> release guidance.")]
    [InlineData("<!-- leading --> Visible <!-- one --> release <!-- two --> guidance. <!-- trailing -->")]
    [InlineData("Visible <!-- internal\n# Hidden heading\n draft note --> release guidance.")]
    public async Task FallbackMetadataOmitsAllHtmlCommentSpans(string summary)
    {
        using var disk = new MemoryTestDirectory();
        var body = summary + "\n\n# Release <!-- inline\nheading note --> plan <!-- unclosed\n# Hidden heading";
        disk.WriteHandwritten(Global, "release.md", body);

        var entry = Assert.Single((await disk.Engine.ReadAsync(GlobalOnly,
            [MemoryRef.Parse("global/release")], int.MaxValue, Ct)).Entries);
        Assert.Equal("Visible release guidance.", entry.Meta.Summary);
        Assert.Equal("Release plan", entry.Meta.Title);
        Assert.Equal(body, entry.Body);
    }

    [Theory]
    [InlineData("area/0")]
    [InlineData("global/a-z_123")]
    public void Canonical_references_round_trip(string text)
    {
        Assert.True(MemoryRef.TryParse(text, out var reference));
        Assert.Equal(text, reference.ToString());
        Assert.Equal(reference, MemoryRef.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("work/key")]
    [InlineData("AREA/key")]
    [InlineData("global/Key")]
    [InlineData("area/key/other")]
    [InlineData("area/key\\other")]
    [InlineData("area/a.b")]
    [InlineData("area/..")]
    [InlineData("area/.hidden")]
    [InlineData("area/-leading")]
    [InlineData("area/_leading")]
    [InlineData("area/é")]
    [InlineData("global/")]
    [InlineData("global/😀")]
    [InlineData(" global/a")]
    [InlineData("global/a\n")]
    [InlineData("global/a%2Fb")]
    public void Reference_parser_rejects_noncanonical_names(string? text)
    {
        Assert.False(MemoryRef.TryParse(text, out _));
        Assert.Throws<FormatException>(() => MemoryRef.Parse(text!));
        Assert.True(MemoryRef.TryParse("global/" + new string('a', 64), out _));
        Assert.False(MemoryRef.TryParse("global/" + new string('a', 65), out _));
    }

    [Fact]
    public async Task Put_round_trips_standard_frontmatter_and_complete_markdown_without_persisting_runtime_metadata()
    {
        using var disk = new MemoryTestDirectory();
        const string body = "# 工程\r\n\r\n<!-- A normal note -->\r\n- Multiple lines\r\n- Keep whitespace.  ";
        var request = new MemoryPutRequest("engineering-notes", "工程 😀: \"R&D\"",
            "Notes #1 \\ engineering", body, Pinned: true,
            Tags: [" Work ", "work", "领域"], Sources: ["https://example.test/a#b", "owner's note"]);
        var result = await disk.Engine.PutAsync(Area, request, Ct);
        var path = Path.Combine(disk.AreaPath, "engineering-notes.md");
        var raw = await File.ReadAllBytesAsync(path, Ct);
        var content = Encoding.UTF8.GetString(raw);
        Assert.StartsWith("---\ntitle: \"工程 😀: \\\"R&D\\\"\"\n", content);
        Assert.Contains("\npinned: true\ntags:\n  - \"work\"\n  - \"领域\"\nsources:\n", content);
        Assert.EndsWith("---\n" + body, content);
        Assert.DoesNotContain("etag:", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updatedAt:", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("perga-memory-v1", content, StringComparison.Ordinal);
        Assert.Equal(Hash(raw), result.Meta.Etag);
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(), result.Meta.UpdatedAtMs);

        IMemoryEngine reopened = new MemoryEngine(disk.Root);
        var entry = Assert.Single((await reopened.ReadAsync(Mixed, [result.Meta.Ref], raw.Length, Ct)).Entries);
        Assert.Equal(content, entry.Content);
        Assert.Equal(request.Title, entry.Meta.Title);
        Assert.Equal(request.Summary, entry.Meta.Summary);
        Assert.True(entry.Meta.Pinned);
        Assert.Equal(["work", "领域"], entry.Meta.Tags);
        Assert.Equal(request.Sources, entry.Meta.Sources);
        Assert.Equal(result.Meta.Etag, entry.Meta.Etag);
        Assert.Equal(result.Meta.UpdatedAtMs, entry.Meta.UpdatedAtMs);
        Assert.Single(Directory.EnumerateFileSystemEntries(disk.AreaPath));
        Assert.False(Directory.Exists(Path.Combine(disk.Root, ".engine")));
    }

    [Fact]
    public async Task Reads_plain_markdown_and_human_authored_frontmatter_without_rewriting_files()
    {
        using var disk = new MemoryTestDirectory();
        const string plain = "\r\n<!-- An ordinary comment -->\r\n\r\n<!--\r\n# Comment heading\r\n-->\r\n"
            + "~~~ md\r\n# Fenced heading\r\n~~~\r\n\r\n# Human heading #\r\nMore text";
        var longSummary = new string('x', 139) + "😀 and more";
        disk.WriteHandwritten(Area, "handwritten.md", plain);
        disk.WriteHandwritten(Area, "no-heading.md", longSummary);
        const string yaml = "\uFEFF---\r\ntitle: 'A person''s note' # note\r\nsummary: Human entry # ignore\r\n"
            + "pinned: TRUE # allow manual pinning\r\ntags: [One, 'two,three', TWO, \"four#five\"] # comment\r\n"
            + "sources: # source list\r\n- https://example.test/#fragment\r\n- 'other source'\r\n"
            + "unrecognized: field\r\n---\r\nBody\r\n";
        var yamlPath = disk.WriteHandwritten(Area, "human-yaml.md", yaml);
        const string incomplete = "---\n# A plain heading\nA document with horizontal rules.\n---\n";
        disk.WriteHandwritten(Area, "horizontal-rule.md", incomplete);

        var all = await disk.Engine.ListAsync(Mixed, Ct);
        var hand = Assert.Single(all, meta => meta.Ref.Key == "handwritten");
        Assert.Equal("Human heading", hand.Title);
        Assert.Equal("~~~ md", hand.Summary);
        Assert.False(hand.Pinned);
        Assert.Empty(hand.Tags);
        var fallback = Assert.Single(all, meta => meta.Ref.Key == "no-heading");
        Assert.Equal("no-heading", fallback.Title);
        Assert.Equal(new string('x', 139), fallback.Summary);
        Assert.Equal("A plain heading", Assert.Single(all, meta => meta.Ref.Key == "horizontal-rule").Title);
        var authored = Assert.Single(all, meta => meta.Ref.Key == "human-yaml");
        Assert.Equal("A person's note", authored.Title);
        Assert.Equal("Human entry", authored.Summary);
        Assert.True(authored.Pinned);
        Assert.Equal(["one", "two,three", "two", "four#five"], authored.Tags);
        Assert.Equal(["https://example.test/#fragment", "other source"], authored.Sources);
        Assert.Equal(Hash(await File.ReadAllBytesAsync(yamlPath, Ct)), authored.Etag);

        var exact = await disk.Engine.ReadAsync(Mixed, [hand.Ref, authored.Ref], int.MaxValue, Ct);
        Assert.Equal(yaml, exact.Entries[0].Content);
        Assert.Equal(plain, exact.Entries[1].Content);
    }

    [Fact]
    public async Task Read_takes_Etag_from_all_bytes_and_time_from_mtime_independently()
    {
        using var disk = new MemoryTestDirectory();
        var path = disk.WriteHandwritten(Global, "a.md", "body");
        var original = Assert.Single(await disk.Engine.ListAsync(GlobalOnly, Ct));
        var later = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, later);
        var retimed = Assert.Single(await disk.Engine.ListAsync(GlobalOnly, Ct));
        Assert.Equal(original.Etag, retimed.Etag);
        Assert.Equal(new DateTimeOffset(later).ToUnixTimeMilliseconds(), retimed.UpdatedAtMs);

        await File.WriteAllTextAsync(path, "body\n<!-- edit -->", Ct);
        File.SetLastWriteTimeUtc(path, later);
        var modified = Assert.Single(await disk.Engine.ListAsync(GlobalOnly, Ct));
        Assert.Equal(retimed.UpdatedAtMs, modified.UpdatedAtMs);
        Assert.NotEqual(retimed.Etag, modified.Etag);
    }

    public static TheoryData<MemoryPutRequest> InvalidRequests => new()
    {
        Request("../entry"),
        Request("file.md"),
        Request("UPPER"),
        Request(new string('a', 65)),
        Request("empty-title") with { Title = "  " },
        Request("long-title") with { Title = new string('a', 81) },
        Request("control-title") with { Title = "first\tsecond" },
        Request("line-title") with { Title = "first\u2028second" },
        Request("empty-summary") with { Summary = "" },
        Request("long-summary") with { Summary = new string('a', 141) },
        Request("control-summary") with { Summary = "first\nsecond" },
        Request("empty-tag") with { Tags = [" "] },
        Request("long-tag") with { Tags = [new string('t', 33)] },
        Request("control-tag") with { Tags = ["a\0"] },
        Request("tag-count") with { Tags = Enumerable.Repeat("same", 9).ToArray() },
        Request("source-count") with { Sources = Enumerable.Repeat("same", 17).ToArray() },
        Request("source-length") with { Sources = [new string('a', 129)] },
        Request("source-line") with { Sources = ["one\r\ntwo"] },
        Request("too-big-utf8", new string('界', 6000)),
        Request("too-big-with-frontmatter", new string('a', 16384)),
        Request("invalid-utf8", "\ud800"),
    };

    [Theory]
    [MemberData(nameof(InvalidRequests), DisableDiscoveryEnumeration = true)]
    public async Task Validation_fails_without_creating_directories(MemoryPutRequest request)
    {
        using var disk = new MemoryTestDirectory();
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.PutAsync(Global, request, Ct));
        Assert.False(Directory.Exists(disk.Root));
    }

    [Fact]
    public async Task Serialized_file_accepts_exact_UTF8_limit_and_overflow_leaves_existing_bytes_unchanged()
    {
        using var disk = new MemoryTestDirectory();
        var request = Request("limit", "");
        var initial = await disk.Engine.PutAsync(Global, request, Ct);
        var path = Path.Combine(disk.GlobalPath, "limit.md");
        var headerBytes = (int)new FileInfo(path).Length;
        var body = "界" + new string('x', 16384 - headerBytes - 3);
        var exact = await disk.Engine.PutAsync(Global, request with { Body = body, ExpectedEtag = initial.Meta.Etag }, Ct);
        Assert.Equal(16384L, new FileInfo(path).Length);

        await Assert.ThrowsAsync<MemoryValidationException>(() =>
            disk.Engine.PutAsync(Global, request with { Body = body + "x", ExpectedEtag = exact.Meta.Etag }, Ct));

        Assert.Equal(exact.Meta.Etag, Hash(await File.ReadAllBytesAsync(path, Ct)));
        Assert.Single(Directory.EnumerateFileSystemEntries(disk.GlobalPath));
    }

    [Fact]
    public async Task Etag_is_required_for_overwrite_and_delete_and_atomic_rename_preserves_an_open_reader()
    {
        using var disk = new MemoryTestDirectory();
        var first = await disk.Engine.PutAsync(Global, Request("record"), Ct);
        var path = Path.Combine(disk.GlobalPath, "record.md");
        var original = await File.ReadAllBytesAsync(path, Ct);
        var missingEtag = await Assert.ThrowsAsync<MemoryConflictException>(() =>
            disk.Engine.PutAsync(Global, Request("record", "replacement"), Ct));
        Assert.Equal(first.Meta.Ref, missingEtag.Ref);
        Assert.Equal(first.Meta.Etag, missingEtag.CurrentEtag);
        var staleEtag = await Assert.ThrowsAsync<MemoryConflictException>(() =>
            disk.Engine.PutAsync(Global, Request("record", "replacement") with { ExpectedEtag = "other" }, Ct));
        Assert.Equal(first.Meta.Etag, staleEtag.CurrentEtag);

        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var replacement = await disk.Engine.PutAsync(Global,
            Request("record", "replacement") with { ExpectedEtag = first.Meta.Etag }, Ct);
        var stillOriginal = new byte[original.Length];
        await reader.ReadExactlyAsync(stillOriginal, Ct);
        Assert.Equal(original, stillOriginal);
        Assert.Equal(-1, reader.ReadByte());
        Assert.Equal(replacement.Meta.Etag, Hash(await File.ReadAllBytesAsync(path, Ct)));
        Assert.Single(Directory.EnumerateFileSystemEntries(disk.GlobalPath));

        foreach (var invalid in new[] { null, first.Meta.Etag })
        {
            var conflict = await Assert.ThrowsAsync<MemoryConflictException>(() => disk.Engine.DeleteAsync(Global, "record", invalid, Ct));
            Assert.Equal(replacement.Meta.Etag, conflict.CurrentEtag);
        }
        Assert.True((await disk.Engine.DeleteAsync(Global, "record", replacement.Meta.Etag, Ct)).Deleted);
        Assert.False((await disk.Engine.DeleteAsync(Global, "record", replacement.Meta.Etag, Ct)).Deleted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(disk.GlobalPath));
    }

    [Fact]
    public async Task Concurrent_updates_across_engine_instances_can_only_commit_one_matching_Etag()
    {
        using var disk = new MemoryTestDirectory();
        var first = await disk.Engine.PutAsync(Area, Request("record"), Ct);
        var otherEngine = new MemoryEngine(disk.Root);
        var one = disk.Engine.PutAsync(Area, Request("record", "one") with { ExpectedEtag = first.Meta.Etag }, Ct);
        var two = otherEngine.PutAsync(Area, Request("record", "two") with { ExpectedEtag = first.Meta.Etag }, Ct);
        await Assert.ThrowsAsync<MemoryConflictException>(() => Task.WhenAll(one, two));

        var succeeded = await Assert.Single(new[] { one, two }, task => task.IsCompletedSuccessfully);
        var failed = Assert.Single(new[] { one, two }, task => task.IsFaulted);
        var conflict = Assert.IsType<MemoryConflictException>(failed.Exception!.InnerException);
        Assert.Equal(succeeded.Meta.Etag, conflict.CurrentEtag);
        var file = Assert.Single(Directory.EnumerateFileSystemEntries(disk.AreaPath));
        Assert.Equal(succeeded.Meta.Etag, Hash(await File.ReadAllBytesAsync(file, Ct)));
    }

    [Theory]
    [InlineData(MemoryScopeKind.Area, 1228)]
    [InlineData(MemoryScopeKind.Global, 820)]
    public async Task Pinned_writes_enforce_default_budget_with_exact_projected_and_current_bytes(MemoryScopeKind kind, int limit)
    {
        using var disk = new MemoryTestDirectory();
        var scope = kind == MemoryScopeKind.Area ? Area : Global;
        var firstRef = new MemoryRef(kind, "full");
        var header = $"### {firstRef} — Title\n";
        var body = new string('x', limit - Bytes(header) - 4) + "界\n";
        var first = await disk.Engine.PutAsync(scope, Request("full", body) with { Pinned = true }, Ct);

        var attempted = Request("extra", "界") with { Pinned = true };
        var refusal = await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.PutAsync(scope, attempted, Ct));
        var budget = Assert.IsType<MemoryPinnedBudget>(refusal.PinnedBudget);
        var needed = Bytes($"### {new MemoryRef(kind, "extra")} — Title\n界\n");
        Assert.Equal(kind, budget.Kind);
        Assert.Equal(limit, budget.LimitBytes);
        Assert.Equal(new MemoryPinnedEntryBytes("full", limit), Assert.Single(budget.CurrentPinned));
        Assert.Equal("extra", budget.RequestedKey);
        Assert.Equal(needed, budget.RequestedBytes);
        Assert.Equal((long)limit + needed, budget.ProjectedBytes);
        Assert.Contains($"full={limit} B", refusal.Message);

        // Replacing a pin charges the replacement, not the old entry a second time.
        var shrink = await disk.Engine.PutAsync(scope, Request("full", "short") with { Pinned = true, ExpectedEtag = first.Meta.Etag }, Ct);
        Assert.True(shrink.Meta.Pinned);
        Assert.True((await disk.Engine.PutAsync(scope, attempted, Ct)).Meta.Pinned);
        Assert.False((await disk.Engine.PutAsync(scope, Request("full", body) with { ExpectedEtag = shrink.Meta.Etag }, Ct)).Meta.Pinned);
    }

    [Fact]
    public async Task Concurrent_pins_cannot_overspend_a_scope_budget()
    {
        using var disk = new MemoryTestDirectory(new MemoryEngineOptions(GlobalPinnedBytes: 50));
        var one = disk.Engine.PutAsync(Global, Request("one", "a reasonable note") with { Pinned = true }, Ct);
        var two = disk.Engine.PutAsync(Global, Request("two", "a reasonable note") with { Pinned = true }, Ct);
        await Assert.ThrowsAsync<MemoryValidationException>(() => Task.WhenAll(one, two));
        Assert.Single(await disk.Engine.ListAsync(GlobalOnly, Ct));
        Assert.Single(Directory.EnumerateFileSystemEntries(disk.GlobalPath));
    }
}
