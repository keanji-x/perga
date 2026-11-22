using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class TrackScheduleIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OverdueCalendarProjectsOnlyOneCatchUpAndPreservesRecordedHistory(int? maxOccurrences)
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: opts =>
        {
            opts.EnableScheduleService = false;
            opts.EnableMaintenanceService = false;
        });
        host.TimeProvider.SetUtcNow(DateTimeOffset.Parse("2026-10-31T00:00:00Z"));
        var (trackId, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        await FlowFixture.AddAsync(kernel, trackId,
            FlowFixture.Wake("daily", "Go", new(Cron: "0 9 * * *", Tz: "UTC", MaxOccurrences: maxOccurrences)));
        var store = host.Services.GetRequiredService<ITrackStore>().TrackSchedules!;
        var recorded = DateTimeOffset.Parse("2026-10-31T09:00:00Z").ToUnixTimeMilliseconds();
        var staleNext = DateTimeOffset.Parse("2026-11-01T09:00:00Z").ToUnixTimeMilliseconds();
        await kernel.FireDueTrackSchedulesAsync(recorded);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        var now = DateTimeOffset.Parse("2026-11-05T10:00:00Z");
        host.TimeProvider.SetUtcNow(now);

        var calendar = await kernel.GetTodayCalendarAsync("2026-10-31", "2026-11-07");
        var items = calendar.Schedules.Where(s => s.Id == "flow:daily").ToArray();
        var expectedDates = maxOccurrences == 2 ? new[] { "2026-10-31", "2026-11-05" }
            : maxOccurrences == 3 ? new[] { "2026-10-31", "2026-11-05", "2026-11-06" }
            : new[] { "2026-10-31", "2026-11-05", "2026-11-06", "2026-11-07" };
        Assert.Equal(expectedDates, items.Select(s => s.Date));
        Assert.Equal(recorded, items[0].NextFireAtMs);
        Assert.Equal(now.ToUnixTimeMilliseconds(), items[1].NextFireAtMs);
        Assert.All(items.Skip(2), s => Assert.Equal(
            DateTimeOffset.Parse($"{s.Date}T09:00:00Z").ToUnixTimeMilliseconds(), s.NextFireAtMs));
        // Projection must not mutate the durable schedule before the catch-up poll.
        var schedule = await store.GetAsync(trackId, "flow:daily", default);
        Assert.Equal(staleNext, schedule!.NextFireAtMs);
        Assert.Equal(1, schedule.FiredCount);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("done")]
    [InlineData("budget")]
    public async Task CalendarUsesRecordedFiresAndConservativelyCapsHighFrequencySchedules(string scenario)
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: opts =>
        {
            opts.EnableScheduleService = false;
            opts.EnableMaintenanceService = false;
        });
        host.TimeProvider.SetUtcNow(DateTimeOffset.Parse("2026-11-01T00:00:00Z"));
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Calendar", DefaultCwd: host.DataDir));
        var snap = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Cwd: host.DataDir));
        await kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(Deadline);
        var store = host.Services.GetRequiredService<ITrackStore>().TrackSchedules!;
        await FlowFixture.AddAsync(kernel, snap.Track.Id,
            FlowFixture.Wake("daily", "Go", new(Cron: "0 9 * * *", Tz: "UTC")),
            FlowFixture.Wake("once", "Go", new(At: "2026-11-02T09:00:00Z")));
        var actualFire = DateTimeOffset.Parse("2026-11-05T10:00:00Z").ToUnixTimeMilliseconds();
        await kernel.FireDueTrackSchedulesAsync(actualFire);
        await kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(Deadline);
        var calendar = await kernel.GetTodayCalendarAsync("2026-11-01", "2026-11-07");
        if (scenario == "history")
        {
            Assert.DoesNotContain(calendar.Schedules, s => s.Date.CompareTo("2026-11-05") < 0);
            Assert.Contains(calendar.Schedules, s => s.Id == "flow:daily" && s.NextFireAtMs == actualFire);
            return;
        }
        if (scenario == "done")
        {
            var done = Assert.Single(calendar.Schedules, s => s.Id == "flow:once");
            Assert.Equal("done", done.State);
            Assert.Equal(actualFire, done.NextFireAtMs);
            Assert.Equal("2026-11-05", done.Date);
            return;
        }
        await FlowFixture.AddAsync(kernel, snap.Track.Id,
            FlowFixture.Wake("seconds", "Go", new(Cron: "* * * * * *", Tz: "UTC", MaxOccurrences: 200_000)));
        calendar = await kernel.GetTodayCalendarAsync("2026-11-01", "2026-11-07");
        // 100,000 evaluation steps conservatively cap before November 3, never expand to November 7.
        Assert.DoesNotContain(calendar.Schedules, s => s.Id == "flow:seconds" && s.Date.CompareTo("2026-11-03") >= 0);
        Assert.Contains(calendar.Schedules, s => s.Id == "flow:seconds");
    }

    [Fact]
    public async Task CalendarRejectsMaximumDateWithBadRequest()
    {
        await using var host = new PergaHostTestCluster();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.Client.GetAsync("/api/today/calendar?start=9999-12-31&end=9999-12-31")).StatusCode);
    }

    [Fact]
    public async Task TodayCalendarHttpEndpointReturnsPresenceSchedulesAndValidatesInputs()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: opts =>
        {
            opts.EnableScheduleService = false;
            opts.EnableMaintenanceService = false;
        });
        var kernel = host.Services.GetRequiredService<IPergaKernel>();

        // Ensure today's daily track exists and populate its report.
        var dailyResp = await host.Client.GetAsync("/api/today/daily");
        dailyResp.EnsureSuccessStatusCode();
        using var dailyDoc = JsonDocument.Parse(await dailyResp.Content.ReadAsStringAsync());
        var todayDate = dailyDoc.RootElement.GetProperty("date").GetString()!;
        var dailyTrackId = dailyDoc.RootElement.GetProperty("track_id").GetString()!;

        var readStore = host.Services.GetRequiredService<ITrackReadStore>();
        var dailyTrack = (await readStore.GetTrackAsync(dailyTrackId, default))!;
        var report = await kernel.ReadReportAsync(dailyTrackId);
        await kernel.CommitReportAsync(dailyTrackId, dailyTrack.LeadSessionId!, new ReportCommitRequest(
            new ReportDoc.ReadAnchor(report.DocRev, new Dictionary<string, int>(), true),
            [new ReportDoc.BlockOp.UpsertBlockOp(null, ReportDoc.ReportBlockKind.Prose, "# Focus\n\nShip calendar integration.", null, null)]));

        // Schedule a reminder on a specific day within the visible range.
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Work", DefaultCwd: host.DataDir));
        var workTrack = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Release Track", Cwd: host.DataDir));
        await kernel.WaitForTrackIdleAsync(workTrack.Track.Id).WaitAsync(Deadline);
        var day0 = DateOnly.ParseExact(todayDate, "yyyy-MM-dd");
        var targetLocalDate = day0.AddDays(1).ToString("yyyy-MM-dd");
        var day2Date = day0.AddDays(2).ToString("yyyy-MM-dd");
        var day3Date = day0.AddDays(3).ToString("yyyy-MM-dd");
        var hostZone = TodayCalendar.Zone(host.TimeProvider);
        var (day1StartMs, _) = TodayCalendar.Bounds(targetLocalDate, hostZone);
        var day1AfternoonMs = DateTimeOffset.FromUnixTimeMilliseconds(day1StartMs).AddHours(14).ToUnixTimeMilliseconds();
        await FlowFixture.AddAsync(kernel, workTrack.Track.Id,
            FlowFixture.Wake("sch_tomorrow", "Check release metrics",
                new(At: DateTimeOffset.FromUnixTimeMilliseconds(day1AfternoonMs).ToString("O")), "Release check-in"));

        // Also schedule a daily 09:00 cron (maxOccurrences = 2) and fire it once to verify:
        // (1) multi-day expansion across the window, (2) already-fired today occurrence stays visible,
        // and (3) expansion stops once maxOccurrences is exhausted.
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(day1StartMs).AddHours(8));
        await FlowFixture.AddAsync(kernel, workTrack.Track.Id,
            FlowFixture.Wake("daily-standup", "Summarize progress",
                new(Cron: "0 9 * * *", Tz: TodayCalendar.IanaId(hostZone), MaxOccurrences: 2), "Daily standup"));
        // Fire day1 09:00 and view calendar at day1 10:00.
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(day1StartMs).AddHours(10));
        var scheduler = host.Services.GetRequiredService<CronosScheduleHostedService>();
        Assert.Equal(1, await scheduler.EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(workTrack.Track.Id).WaitAsync(Deadline);

        // Also test a mid-day created multi-fire cron ("0 9,15 * * *" created at 10:00 -> upcoming at 15:00 today)
        // and a high-frequency cron with maxOccurrences > 2048 so maxStepBudget does not truncate Day 3.
        await FlowFixture.AddAsync(kernel, workTrack.Track.Id,
            FlowFixture.Wake("midday-multi", "Check queue", new(Cron: "0 9,15 * * *", Tz: TodayCalendar.IanaId(hostZone)), "Twice daily"));
        await FlowFixture.AddAsync(kernel, workTrack.Track.Id,
            FlowFixture.Wake("every-minute-budget", "Ping",
                new(Cron: "* * * * *", Tz: TodayCalendar.IanaId(hostZone), MaxOccurrences: 5000), "Heartbeat"));

        var calResp = await host.Client.GetAsync($"/api/today/calendar?start={todayDate}&end={day3Date}");
        calResp.EnsureSuccessStatusCode();
        using var calDoc = JsonDocument.Parse(await calResp.Content.ReadAsStringAsync());
        Assert.Equal(targetLocalDate, calDoc.RootElement.GetProperty("today").GetString());
        var days = calDoc.RootElement.GetProperty("days");
        Assert.Equal(4, days.GetArrayLength());
        Assert.Equal(todayDate, days[0].GetProperty("date").GetString());
        Assert.True(days[0].GetProperty("has_daily_track").GetBoolean());
        Assert.True(days[0].GetProperty("has_report").GetBoolean());
        Assert.Equal(targetLocalDate, days[1].GetProperty("date").GetString());
        Assert.False(days[1].GetProperty("has_daily_track").GetBoolean());

        var items = calDoc.RootElement.GetProperty("schedules").EnumerateArray().ToArray();
        // Expect 1 one-shot on day1 + 2 expanded daily-standup occurrences (day1 already-fired at 09:00 + day2 upcoming at 09:00; NOT day3 because maxOccurrences=2).
        var standups = items.Where(i => i.GetProperty("id").GetString() == "flow:daily-standup").ToArray();
        Assert.Equal(2, standups.Length);
        Assert.Equal(targetLocalDate, standups[0].GetProperty("date").GetString());
        Assert.Equal(day2Date, standups[1].GetProperty("date").GetString());

        // Mid-day created multi-fire cron must appear on day1 (at 15:00), day2, and day3.
        var middayDates = items.Where(i => i.GetProperty("id").GetString() == "flow:midday-multi")
            .Select(i => i.GetProperty("date").GetString()!).ToArray();
        Assert.Equal([targetLocalDate, day2Date, day3Date], middayDates);

        // High-frequency cron with 5000 remaining minutes (~3.47 days) must NOT be truncated before day3 by maxStepBudget.
        var heartbeatDates = items.Where(i => i.GetProperty("id").GetString() == "flow:every-minute-budget")
            .Select(i => i.GetProperty("date").GetString()!).ToArray();
        Assert.Equal([targetLocalDate, day2Date, day3Date], heartbeatDates);

        var checkIn = Assert.Single(items, i => i.GetProperty("id").GetString() == "flow:sch_tomorrow");
        Assert.Equal(targetLocalDate, checkIn.GetProperty("date").GetString());
        Assert.Equal("Release check-in", checkIn.GetProperty("title").GetString());
        Assert.Equal("Release Track", checkIn.GetProperty("track_title").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/today/calendar?start=2026-11-20&end=2026-11-19")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/today/calendar?start=2026-01-01&end=2026-04-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/today/calendar?start=invalid&end=2026-11-20")).StatusCode);
    }
}
