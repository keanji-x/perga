using static Perga.MemoryKit.Tests.MemoryTestDirectory;

namespace Perga.MemoryKit.Tests;

public sealed class MemoryPathTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".engine")]
    [InlineData("work.v2")]
    [InlineData("some/other")]
    [InlineData("some\\other")]
    [InlineData("/tmp")]
    [InlineData("C:\\elsewhere")]
    [InlineData("line\nbreak")]
    [InlineData("nul\0byte")]
    [InlineData(" trailing ")]
    [InlineData("CON")]
    public async Task Area_ids_must_be_safe_single_segments_for_writes_and_cleanup(string? areaId)
    {
        using var disk = new MemoryTestDirectory();
        var scope = new MemoryWriteScope(MemoryScopeKind.Area, areaId);
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.PutAsync(scope, Request("entry"), Ct));
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.DeleteAsync(scope, "entry", null, Ct));
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.DeleteAreaDirectoryAsync(areaId!, Ct));
        Assert.False(Directory.Exists(disk.Root));
    }

    [Fact]
    public async Task Scope_rules_reject_invalid_kinds_global_with_area_and_invalid_refs()
    {
        using var disk = new MemoryTestDirectory();
        await Assert.ThrowsAsync<MemoryValidationException>(() =>
            disk.Engine.PutAsync(new MemoryWriteScope(MemoryScopeKind.Global, "work"), Request("entry"), Ct));
        await Assert.ThrowsAsync<MemoryValidationException>(() =>
            disk.Engine.PutAsync(new MemoryWriteScope((MemoryScopeKind)12), Request("entry"), Ct));
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.ReadAsync(Mixed, [default], 10, Ct));
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.ListAsync(new MemoryReadScope("../work"), Ct));
        Assert.False(Directory.Exists(disk.Root));
    }

    [Fact]
    public async Task Enumeration_ignores_dot_entries_unrelated_files_and_subdirectories()
    {
        using var disk = new MemoryTestDirectory();
        var missing = await disk.Engine.ListAsync(Mixed, Ct);
        Assert.Empty(missing);
        Assert.False(Directory.Exists(disk.Root));

        disk.WriteHandwritten(Area, "valid.md", "body");
        disk.WriteHandwritten(Area, ".hidden.md", "ignored");
        disk.WriteHandwritten(Area, ".atomic.tmp", "ignored");
        disk.WriteHandwritten(Area, "not-markdown.txt", "ignored");
        disk.WriteHandwritten(Area, "uppercase.MD", "ignored");
        disk.WriteHandwritten(Area, "INVALID.md", "ignored");
        Directory.CreateDirectory(Path.Combine(disk.Root, ".engine"));
        File.WriteAllText(Path.Combine(disk.Root, ".engine", "stale.md"), "ignored");
        Directory.CreateDirectory(Path.Combine(disk.AreaPath, "nested.md"));
        File.WriteAllText(Path.Combine(disk.AreaPath, "nested.md", "descendant.md"), "ignored");

        var entry = Assert.Single(await disk.Engine.ListAsync(Mixed, Ct));
        Assert.Equal("area/valid", entry.Ref.ToString());
    }

    [Fact]
    public async Task Entry_symlinks_and_dangling_links_are_unavailable_and_never_updated_or_deleted()
    {
        using var disk = new MemoryTestDirectory();
        var good = disk.WriteHandwritten(Area, "good.md", "valid");
        var external = Path.Combine(disk.Sandbox, "external.md");
        File.WriteAllText(external, "outside");
        File.CreateSymbolicLink(Path.Combine(disk.AreaPath, "link.md"), external);
        File.CreateSymbolicLink(Path.Combine(disk.AreaPath, "dangling.md"), Path.Combine(disk.Sandbox, "missing.md"));
        File.CreateSymbolicLink(Path.Combine(disk.AreaPath, "inside.md"), good);

        Assert.Equal("good", Assert.Single(await disk.Engine.ListAsync(Mixed, Ct)).Ref.Key);
        MemoryRef[] refs = [MemoryRef.Parse("area/link"), MemoryRef.Parse("area/dangling"), MemoryRef.Parse("area/inside")];
        var read = await disk.Engine.ReadAsync(Mixed, refs, int.MaxValue, Ct);
        Assert.Empty(read.Entries);
        Assert.Equal(["dangling", "inside", "link"], read.NotFound.Select(reference => reference.Key));

        foreach (var reference in refs)
        {
            await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.PutAsync(Area, Request(reference.Key), Ct));
            await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.DeleteAsync(Area, reference.Key, null, Ct));
        }
        Assert.Equal("outside", File.ReadAllText(external));
        Assert.False(File.Exists(Path.Combine(disk.Sandbox, "missing.md")));
        Assert.Equal("valid", File.ReadAllText(good));
    }

    [Fact]
    public async Task Reads_allow_one_mebibyte_and_count_oversized_entries_as_unreadable()
    {
        const int maxReadBytes = 1024 * 1024;
        using var disk = new MemoryTestDirectory();
        disk.WriteHandwritten(Area, "allowed.md", new string('a', maxReadBytes));
        disk.WriteHandwritten(Area, "oversized.md", new string('b', maxReadBytes + 1));

        var snapshot = await disk.Engine.GetContextSnapshotAsync("work", new MemoryContextBudget(), Ct);
        Assert.Equal(2, snapshot.SourceEntryCount);
        Assert.Equal(1, snapshot.UnreadableEntryCount);
        MemoryRef[] refs = [MemoryRef.Parse("area/allowed"), MemoryRef.Parse("area/oversized")];
        var read = await disk.Engine.ReadAsync(Mixed, refs, int.MaxValue, Ct);
        Assert.Equal(maxReadBytes, Assert.Single(read.Entries).SizeBytes);
        Assert.Equal(refs[1], Assert.Single(read.NotFound));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("global")]
    [InlineData("areas")]
    [InlineData("area")]
    public async Task Linked_root_or_scope_components_are_never_followed(string component)
    {
        using var disk = new MemoryTestDirectory();
        var external = Path.Combine(disk.Sandbox, "external");
        Directory.CreateDirectory(external);
        var location = component switch
        {
            "root" => disk.Root,
            "global" => disk.GlobalPath,
            "areas" => Path.Combine(disk.Root, "areas"),
            _ => disk.AreaPath,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(location)!);
        Directory.CreateSymbolicLink(location, external);
        var scope = component == "global" ? Global : Area;
        var readScope = scope == Global ? GlobalOnly : Mixed;
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.ListAsync(readScope, Ct));
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.PutAsync(scope, Request("entry"), Ct));
        await Assert.ThrowsAsync<MemoryValidationException>(() => disk.Engine.DeleteAsync(scope, "entry", null, Ct));
        await disk.Engine.DeleteAreaDirectoryAsync("work", Ct);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        Assert.NotNull(new DirectoryInfo(location).LinkTarget);
    }

    [Fact]
    public async Task Area_cleanup_removes_only_the_requested_area_and_does_not_follow_nested_links()
    {
        using var disk = new MemoryTestDirectory();
        var external = Path.Combine(disk.Sandbox, "external");
        Directory.CreateDirectory(external);
        var outside = Path.Combine(external, "sentinel.md");
        var global = disk.WriteHandwritten(Global, "global.md", "keep");
        var other = disk.WriteHandwritten(new MemoryWriteScope(MemoryScopeKind.Area, "other"), "other.md", "keep");
        File.WriteAllText(outside, "keep");
        disk.WriteHandwritten(Area, "remove.md", "remove");
        Directory.CreateSymbolicLink(Path.Combine(disk.AreaPath, "linked"), external);

        await disk.Engine.DeleteAreaDirectoryAsync("work", Ct);
        await disk.Engine.DeleteAreaDirectoryAsync("work", Ct);
        Assert.False(Directory.Exists(disk.AreaPath));
        Assert.Equal("keep", File.ReadAllText(outside));
        Assert.Equal("keep", File.ReadAllText(global));
        Assert.Equal("keep", File.ReadAllText(other));
    }

    [Fact]
    public async Task Orphan_cleanup_retains_case_aliases_of_live_areas()
    {
        using var disk = new MemoryTestDirectory();
        var alias = disk.WriteHandwritten(new MemoryWriteScope(MemoryScopeKind.Area, "WORK"), "entry.md", "keep");
        var orphan = disk.WriteHandwritten(new MemoryWriteScope(MemoryScopeKind.Area, "orphan"), "entry.md", "remove");

        await disk.Engine.CleanupOrphanAreaDirectoriesAsync(new HashSet<string>(StringComparer.Ordinal) { "work" }, Ct);

        Assert.Equal("keep", File.ReadAllText(alias));
        Assert.False(Directory.Exists(Path.GetDirectoryName(orphan)));
    }

    [Fact]
    public async Task New_memory_directories_and_files_keep_owner_only_modes_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var disk = new MemoryTestDirectory();
        await disk.Engine.PutAsync(Area, Request("entry"), Ct);

        const UnixFileMode nonOwnerAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(disk.Root) & nonOwnerAccess);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(Path.Combine(disk.Root, "areas")) & nonOwnerAccess);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(disk.AreaPath) & nonOwnerAccess);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(Path.Combine(disk.AreaPath, "entry.md")) & nonOwnerAccess);
    }

    [Fact]
    public async Task Cancelled_mutations_leave_committed_bytes_and_no_temporary_files()
    {
        using var disk = new MemoryTestDirectory();
        var original = await disk.Engine.PutAsync(Area, Request("entry"), Ct);
        var path = Path.Combine(disk.AreaPath, "entry.md");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disk.Engine.PutAsync(Area,
            Request("entry", "replace") with { ExpectedEtag = original.Meta.Etag }, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disk.Engine.DeleteAsync(Area, "entry", original.Meta.Etag, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disk.Engine.DeleteAreaDirectoryAsync("work", cancelled.Token));
        Assert.Equal(original.Meta.Etag, Hash(await File.ReadAllBytesAsync(path, Ct)));
        Assert.Single(Directory.EnumerateFileSystemEntries(disk.AreaPath));
    }
}
