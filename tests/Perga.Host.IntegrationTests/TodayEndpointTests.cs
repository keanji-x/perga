using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Host.IntegrationTests;

public sealed class TodayEndpointTests
{
    [Theory]
    [InlineData("/api/today/daily")]
    [InlineData("/api/today/calendar")]
    [InlineData("/api/today/report-changes")]
    [InlineData("/api/today/report-edits")]
    public async Task Retired_today_routes_are_not_served(string path)
    {
        await using var host = new PergaHostTestCluster();
        using var response = await host.AuthenticatedClient.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var anonymous = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task DailyEnsureHasReportAndLeadAndExplicitReadsDoNotCreate()
    {
        var directory = Directory.CreateTempSubdirectory("today-restart-").FullName;
        try
        {
            string id;
            string date;
            await using (var host = new PergaHostTestCluster(directory))
            {
                Assert.Equal(JsonValueKind.Null, (await Get(host, "/api/v1/today/daily?date=2026-11-16")).ValueKind);
                var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Get(host, "/api/v1/today/daily")));
                Assert.Single(replies.Select(r => r.GetProperty("trackId").GetString()).Distinct());
                id = replies[0].GetProperty("trackId").GetString()!;
                date = replies[0].GetProperty("date").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(replies[0].GetProperty("timeZone").GetString()));
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var snapshot = await kernel.GetTrackAggregateAsync(id);
                Assert.Single(snapshot.Sessions);
                Assert.True(snapshot.Sessions[0].IsLead);
                Assert.Equal(snapshot.Track.LeadSessionId, snapshot.Sessions[0].Id);
                Assert.Empty(snapshot.Report.Blocks);
                Assert.Empty((await Get(host, "/api/v1/areas")).EnumerateArray());
                Assert.Equal(id, (await Get(host, $"/api/v1/today/daily?date={date}")).GetProperty("trackId").GetString());
                host.TimeProvider.Advance(TimeSpan.FromDays(1));
                Assert.NotEqual(id, (await Get(host, "/api/v1/today/daily")).GetProperty("trackId").GetString());
            }
            await using var reopened = new PergaHostTestCluster(directory);
            Assert.Equal(id, (await Get(reopened, $"/api/v1/today/daily?date={date}")).GetProperty("trackId").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("/api/v1/today/daily?date=2026-02-30")]
    [InlineData("/api/v1/today/daily?date=2026-1-01")]
    [InlineData("/api/v1/today/daily?date=")]
    [InlineData("/api/v1/today/report-changes?date=invalid")]
    [InlineData("/api/v1/today/report-changes?date=2026-11-17&cursor=x")]
    [InlineData("/api/v1/today/report-changes?date=2026-11-17&throughEventId=0&cursor=")]
    [InlineData("/api/v1/today/report-changes?date=2026-11-17&throughEventId=0&cursor=%20")]
    [InlineData("/api/v1/today/report-changes?date=2026-11-17&throughEventId=0&cursor=x")]
    [InlineData("/api/v1/today/report-changes?date=2026-11-17&throughEventId=-1")]
    [InlineData("/api/v1/today/report-edits?date=2026-11-17&trackId=t&throughEventId=0&cursor=x")]
    public async Task InvalidInputsReturn400(string path)
    {
        await using var host = new PergaHostTestCluster();
        using var response = await host.AuthenticatedClient.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_argument", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ChangesAndEditsUseDateSnapshotBodiesAndHideSystemTracks()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new("Project"));
        var track = await kernel.CreateTrackAsync(new(area.Id, Title: "Evidence"));
        var daily = await Get(host, "/api/v1/today/daily");
        var date = daily.GetProperty("date").GetString()!;
        var store = host.Services.GetRequiredService<ITrackStore>();
        var engine = new ReportDocEngine();
        async Task Save(string id, int rev, string summary, string text)
        {
            var doc = engine.Parse(text, rev, summary);
            var at = host.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await store.SaveReportAsync(id, doc, engine.CompressZstd(doc.Blocks), null, "edit",
                new(null, area.Id, id, "report.updated",
                    JsonSerializer.SerializeToElement(new { docRev = rev, summary }), at), default);
        }
        await Save(track.Track.Id, 1, "First", "# Work\n\nBefore.\n");
        await Save(track.Track.Id, 2, "Second", "# Work\n\nAfter.\n");
        await Save(daily.GetProperty("trackId").GetString()!, 1, "Private", "System body");
        var changes = await Get(host, $"/api/v1/today/report-changes?date={date}");
        var change = Assert.Single(changes.GetProperty("changes").EnumerateArray());
        Assert.Equal(2, change.GetProperty("editCount").GetInt32());
        Assert.Equal("Second", change.GetProperty("summaryAfter").GetString());
        Assert.Contains("+After.", change.GetProperty("patch").GetString());
        var tip = changes.GetProperty("throughEventId").GetInt64();
        await Save(track.Track.Id, 3, "Later", "Later body");
        var edits = await Get(host, $"/api/v1/today/report-edits?date={date}&trackId={track.Track.Id}&throughEventId={tip}");
        Assert.Equal(2, edits.GetProperty("edits").GetArrayLength());
        var edit = edits.GetProperty("edits")[1].GetProperty("edit");
        Assert.Equal("First", edit.GetProperty("summaryBefore").GetString());
        Assert.Equal("Second", edit.GetProperty("summaryAfter").GetString());
        Assert.Contains("Before.", edit.GetProperty("bodyBefore").GetString());
        Assert.Contains("After.", edit.GetProperty("bodyAfter").GetString());
        var previous = TodayCalendar.ParseDate(date).AddDays(-1).ToString("yyyy-MM-dd");
        Assert.Empty((await Get(host, $"/api/v1/today/report-changes?date={previous}")).GetProperty("changes").EnumerateArray());
        var system = await Get(host, $"/api/v1/today/report-edits?date={date}&trackId={daily.GetProperty("trackId").GetString()}&throughEventId={tip}");
        Assert.Empty(system.GetProperty("edits").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await host.AuthenticatedClient.GetAsync("/api/v1/today/supervisor")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.AuthenticatedClient.GetAsync("/api/v1/today/launchpad")).StatusCode);
    }

    private static async Task<JsonElement> Get(PergaHostTestCluster host, string path)
    {
        using var response = await host.AuthenticatedClient.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
