using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class KernelMcpTrackDiscoveryTests
{
    [Theory]
    [InlineData("""{"area":"*"}""")]
    [InlineData("""{"includeClosed":true}""")]
    [InlineData("""{"query":"restricted"}""")]
    public async Task TrackList_BypassesBeforeButStillValidatesArguments(string args)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, caller) = await ctx.CreateDefaultTrackAsync();
        const string source = """
            export default defineTemplate({
              id: 'discovery-policy', title: 'Discovery policy',
              before(action) {
                const args = action.arguments;
                return action.type === 'tool.track_list' &&
                  (args.area === '*' || args.includeClosed === true || args.query === 'restricted')
                  ? Deny('Discovery scope is restricted') : Allow();
              }
            });
            """;
        var runtimeCaller = await ctx.OpenMcpCallerAsync(caller.Track.LeadSessionId!);
        await ctx.Kernel.InvokeKernelMcpToolAsync(runtimeCaller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source }));
        var result = await Call(ctx, caller, KernelMcpToolNames.TrackList, args);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("tracks").ValueKind);
        foreach (var invalid in new[] { """{"AREA":"*"}""", """{"unknown":true}""" })
        {
            var error = await Assert.ThrowsAsync<KernelException>(() =>
                Call(ctx, caller, KernelMcpToolNames.TrackList, invalid));
            Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
        }
    }

    [Fact]
    public async Task AreaList_ReturnsOrderedCountsAndMarksCurrentAreaWithoutActivatingPeers()
    {
        await using var ctx = new KernelTestContext();
        var (current, caller) = await ctx.CreateDefaultTrackAsync();
        var other = await CreateAreaAsync(ctx, "Research", sortOrder: -1);
        var empty = await CreateAreaAsync(ctx, "Later", sortOrder: 1);
        var closed = await CreateTrackAsync(ctx, current, "Closed");
        var peer = await CreateTrackAsync(ctx, other, "Peer");
        await ctx.Kernel.CloseTrackAsync(closed.Track.Id, closed.Track.LeadSessionId!);
        await EvictAsync(ctx, closed.Track.Id);
        await EvictAsync(ctx, peer.Track.Id);

        var listTracksBefore = ctx.Store.ListTracksCalls;
        var result = await Call(ctx, caller, KernelMcpToolNames.AreaList, "{}");
        Assert.Equal(listTracksBefore, ctx.Store.ListTracksCalls);
        Assert.Equal(current.Id, result.GetProperty("currentAreaId").GetString());
        Assert.Collection(result.GetProperty("areas").EnumerateArray(),
            entry => AssertArea(entry, other, false, 1, 0),
            entry => AssertArea(entry, current, true, 1, 1),
            entry => AssertArea(entry, empty, false, 0, 0));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(closed.Track.Id));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(peer.Track.Id));
    }

    [Fact]
    public async Task TrackList_ResolvesScopeFiltersAndReturnsReportSummariesInStoreOrder()
    {
        await using var ctx = new KernelTestContext();
        var (current, caller) = await ctx.CreateDefaultTrackAsync();
        var research = await CreateAreaAsync(ctx, "Research");
        var closed = await CreateTrackAsync(ctx, current, "Needle archive", sortOrder: -1);
        var open = await CreateTrackAsync(ctx, current, "Notes", sortOrder: 1);
        var peer = await CreateTrackAsync(ctx, research, "Peer", sortOrder: 2);
        var read = await ctx.Kernel.ReadReportAsync(open.Track.Id);
        await ctx.Kernel.CommitReportAsync(open.Track.Id, open.Track.LeadSessionId!, new ReportCommitRequest(
            read.Anchor, [new Perga.ReportDoc.BlockOp.ReplaceSectionOp("Overview", "Notes body")], Summary: "Needle summary"));
        await ctx.Kernel.CloseTrackAsync(closed.Track.Id, closed.Track.LeadSessionId!);
        await EvictAsync(ctx, open.Track.Id);
        await EvictAsync(ctx, closed.Track.Id);
        await EvictAsync(ctx, peer.Track.Id);

        Task<JsonElement> List(string arguments) => Call(ctx, caller, KernelMcpToolNames.TrackList, arguments);

        var defaultList = await List("{}");
        AssertScope(defaultList, "current_area", current);
        Assert.Equal([caller.Track.Id, open.Track.Id], TrackIds(defaultList));
        var currentEntry = defaultList.GetProperty("tracks")[0];
        Assert.True(currentEntry.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(current.Id, currentEntry.GetProperty("areaId").GetString());
        Assert.Equal(current.Name, currentEntry.GetProperty("areaName").GetString());
        Assert.Equal(caller.Track.Title, currentEntry.GetProperty("title").GetString());
        Assert.Equal(caller.Track.UpdatedAtMs, currentEntry.GetProperty("updatedAtMs").GetInt64());
        Assert.Equal(JsonValueKind.Null, currentEntry.GetProperty("closedAtMs").ValueKind);
        var notes = defaultList.GetProperty("tracks")[1];
        Assert.False(notes.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("Needle summary", notes.GetProperty("reportSummary").GetString());

        var byId = await List($$"""{"area":"{{research.Id}}"}""");
        AssertScope(byId, "area", research);
        Assert.Equal([peer.Track.Id], TrackIds(byId));
        Assert.Equal(byId.GetRawText(), (await List("""{"area":"  rEsEaRcH  "}""")).GetRawText());

        var all = await List("""{"area":"*","includeClosed":true}""");
        Assert.Equal("all", all.GetProperty("scope").GetString());
        Assert.False(all.TryGetProperty("area", out var area) && area.ValueKind != JsonValueKind.Null);
        Assert.Equal([closed.Track.Id, caller.Track.Id, open.Track.Id, peer.Track.Id], TrackIds(all));
        Assert.True(all.GetProperty("tracks")[0].GetProperty("closedAtMs").GetInt64() > 0);

        Assert.Equal([open.Track.Id], TrackIds(await List("""{"query":"nEeDLe"}""")));
        Assert.Equal([closed.Track.Id, open.Track.Id],
            TrackIds(await List("""{"query":"nEeDLe","includeClosed":true}""")));
        Assert.Equal([peer.Track.Id], TrackIds(await List($$"""{"area":"*","query":"{{peer.Track.Id.ToUpperInvariant()}}"}""")));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(open.Track.Id));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(closed.Track.Id));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(peer.Track.Id));
    }

    [Fact]
    public async Task TrackList_DistinguishesExactIdAmbiguousNameAndMissingArea()
    {
        await using var ctx = new KernelTestContext();
        var (_, caller) = await ctx.CreateDefaultTrackAsync();
        var first = await CreateAreaAsync(ctx, "Research");
        var second = await CreateAreaAsync(ctx, "research");
        await CreateAreaAsync(ctx, first.Id);

        Task<JsonElement> List(string arguments) => Call(ctx, caller, KernelMcpToolNames.TrackList, arguments);

        var exact = await List($$"""{"area":"{{first.Id}}"}""");
        AssertScope(exact, "area", first);
        Assert.Empty(exact.GetProperty("tracks").EnumerateArray());

        var ambiguous = await Assert.ThrowsAsync<KernelException>(() => List("""{"area":" RESEARCH "}"""));
        Assert.Equal(KernelErrorCode.InvalidArgument, ambiguous.Code);
        Assert.Contains(first.Id, ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains(second.Id, ambiguous.Message, StringComparison.Ordinal);

        var missing = await Assert.ThrowsAsync<KernelException>(() => List("""{"area":"missing"}"""));
        Assert.Equal(KernelErrorCode.NotFound, missing.Code);
        Assert.Equal("Area 'missing' not found. Call area_list to inspect available Areas.", missing.Message);
    }

    [Theory]
    [InlineData(KernelMcpToolNames.AreaList, """{"unknown":true}""")]
    [InlineData(KernelMcpToolNames.AreaList, "[]")]
    [InlineData(KernelMcpToolNames.TrackList, """{"unknown":true}""")]
    [InlineData(KernelMcpToolNames.TrackList, """{"area":" "}""")]
    [InlineData(KernelMcpToolNames.TrackList, """{"area":5}""")]
    [InlineData(KernelMcpToolNames.TrackList, """{"query":" "}""")]
    [InlineData(KernelMcpToolNames.TrackList, """{"query":null}""")]
    [InlineData(KernelMcpToolNames.TrackList, """{"includeClosed":"true"}""")]
    [InlineData(KernelMcpToolNames.TrackList, """{"includeClosed":null}""")]
    public async Task Discovery_RejectsInvalidArguments(string tool, string arguments)
    {
        await using var ctx = new KernelTestContext();
        var (_, caller) = await ctx.CreateDefaultTrackAsync();
        var error = await Assert.ThrowsAsync<KernelException>(() => Call(ctx, caller, tool, arguments));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
    }

    private static Task<JsonElement> Call(KernelTestContext ctx, TrackAggregateSnapshot caller, string tool, string args) =>
        ctx.Kernel.InvokeKernelMcpToolAsync(new("token", caller.Track.Id, caller.Track.LeadSessionId!, true, null, null, []),
            tool, TestJson.Parse(args));

    private static Task<AreaDto> CreateAreaAsync(KernelTestContext ctx, string name, double sortOrder = 0) =>
        ctx.Kernel.CreateAreaAsync(new CreateAreaRequest(name, DefaultCwd: "/workspace/demo", SortOrder: sortOrder));

    private static Task<TrackAggregateSnapshot> CreateTrackAsync(KernelTestContext ctx, AreaDto area, string title, double sortOrder = 0) =>
        ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: title, SortOrder: sortOrder));

    private static async Task EvictAsync(KernelTestContext ctx, string trackId)
    {
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(trackId);
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));
    }

    private static string[] TrackIds(JsonElement result) => result.GetProperty("tracks").EnumerateArray()
        .Select(entry => entry.GetProperty("id").GetString()!).ToArray();

    private static void AssertScope(JsonElement result, string scope, AreaDto area)
    {
        Assert.Equal(scope, result.GetProperty("scope").GetString());
        Assert.Equal(area.Id, result.GetProperty("area").GetProperty("id").GetString());
        Assert.Equal(area.Name, result.GetProperty("area").GetProperty("name").GetString());
    }

    private static void AssertArea(JsonElement entry, AreaDto area, bool current, int openCount, int closedCount)
    {
        Assert.Equal(area.Id, entry.GetProperty("id").GetString());
        Assert.Equal(area.Name, entry.GetProperty("name").GetString());
        Assert.Equal(current, entry.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(openCount, entry.GetProperty("openTrackCount").GetInt32());
        Assert.Equal(closedCount, entry.GetProperty("closedTrackCount").GetInt32());
        Assert.Equal(area.DefaultCwd, entry.GetProperty("defaultCwd").GetString());
    }
}
