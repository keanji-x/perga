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
        await kernel.SetTrackScheduleAsync(trackId, new("Daily", "Go", Id: "daily",
            CronExpr: "0 9 * * *", Timezone: "UTC", MaxOccurrences: maxOccurrences));
        var store = host.Services.GetRequiredService<ITrackStore>().TrackSchedules!;
        var recorded = DateTimeOffset.Parse("2026-10-31T09:00:00Z").ToUnixTimeMilliseconds();
        var staleNext = DateTimeOffset.Parse("2026-11-01T09:00:00Z").ToUnixTimeMilliseconds();
        await store.FireDueAsync(recorded, _ => staleNext, default);
        var now = DateTimeOffset.Parse("2026-11-05T10:00:00Z");
        host.TimeProvider.SetUtcNow(now);

        var calendar = await kernel.GetTodayCalendarAsync("2026-10-31", "2026-11-07");
        var items = calendar.Schedules.Where(s => s.Id == "daily").ToArray();
        var expectedDates = maxOccurrences == 2 ? new[] { "2026-10-31", "2026-11-05" }
            : maxOccurrences == 3 ? new[] { "2026-10-31", "2026-11-05", "2026-11-06" }
            : new[] { "2026-10-31", "2026-11-05", "2026-11-06", "2026-11-07" };
        Assert.Equal(expectedDates, items.Select(s => s.Date));
        Assert.Equal(recorded, items[0].NextFireAtMs);
        Assert.Equal(now.ToUnixTimeMilliseconds(), items[1].NextFireAtMs);
        Assert.All(items.Skip(2), s => Assert.Equal(
            DateTimeOffset.Parse($"{s.Date}T09:00:00Z").ToUnixTimeMilliseconds(), s.NextFireAtMs));
        // Projection must not mutate the durable schedule before the catch-up poll.
        var schedule = await store.GetAsync(trackId, "daily", default);
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
        await kernel.SetTrackScheduleAsync(snap.Track.Id, new("Daily", "Go", Id: "daily", CronExpr: "0 9 * * *", Timezone: "UTC"));
        await kernel.SetTrackScheduleAsync(snap.Track.Id, new("Once", "Go", Id: "once", RunAt: "2026-11-02T09:00:00Z"));
        var actualFire = DateTimeOffset.Parse("2026-11-05T10:00:00Z").ToUnixTimeMilliseconds();
        await store.FireDueAsync(actualFire, _ => DateTimeOffset.Parse("2026-11-06T09:00:00Z").ToUnixTimeMilliseconds(), default);
        var calendar = await kernel.GetTodayCalendarAsync("2026-11-01", "2026-11-07");
        if (scenario == "history")
        {
            Assert.DoesNotContain(calendar.Schedules, s => s.Date.CompareTo("2026-11-05") < 0);
            Assert.Contains(calendar.Schedules, s => s.Id == "daily" && s.NextFireAtMs == actualFire);
            return;
        }
        if (scenario == "done")
        {
            var done = Assert.Single(calendar.Schedules, s => s.Id == "once");
            Assert.Equal("done", done.State);
            Assert.Equal(actualFire, done.NextFireAtMs);
            Assert.Equal("2026-11-05", done.Date);
            return;
        }
        await kernel.SetTrackScheduleAsync(snap.Track.Id, new("Seconds", "Go", Id: "seconds", CronExpr: "* * * * * *", Timezone: "UTC", MaxOccurrences: 200_000));
        calendar = await kernel.GetTodayCalendarAsync("2026-11-01", "2026-11-07");
        // 100,000 evaluation steps conservatively cap before November 3, never expand to November 7.
        Assert.DoesNotContain(calendar.Schedules, s => s.Id == "seconds" && s.Date.CompareTo("2026-11-03") >= 0);
        Assert.Contains(calendar.Schedules, s => s.Id == "seconds");
    }

    [Fact]
    public async Task CalendarRejectsMaximumDateWithBadRequest()
    {
        await using var host = new PergaHostTestCluster();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.Client.GetAsync("/api/today/calendar?start=9999-12-31&end=9999-12-31")).StatusCode);
    }

    [Fact]
    public async Task McpScheduleToolsEnforcePermissionsAndFireThroughCronosIntoLeadTurns()
    {
        await using var cluster = new PergaHostTestCluster(configureHostOptions: opts =>
        {
            opts.EnableScheduleService = false;
            opts.EnableMaintenanceService = false;
        });
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Schedules", DefaultCwd: cluster.DataDir));
        var snap = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Ops Track", Cwd: cluster.DataDir));
        var trackId = snap.Track.Id;
        var leadSessionId = snap.Track.LeadSessionId!;
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        var store = cluster.Services.GetRequiredService<ITrackStore>();
        var nowMs = cluster.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await store.UpsertSessionAsync(new AgentSessionDto(
            Id: "ses_worker",
            TrackId: trackId,
            IsLead: false,
            BoundTaskId: "tsk_1",
            BoundAttemptId: "att_1",
            Title: "Contract Worker",
            Provider: "codex",
            Model: null,
            NativeThreadId: null,
            Epoch: 1,
            Status: "running",
            ArchivedAtMs: null,
            CreatedAtMs: nowMs,
            UpdatedAtMs: nowMs), null, default);
        await kernel.Actors.RemoveAndDisposeAsync(trackId);
        var leadCaller = new McpSessionPrincipal("lead-tok", trackId, leadSessionId, true, null, null, []);
        var workerCaller = new McpSessionPrincipal("worker-tok", trackId, "ses_worker", false, "att_1", null, []);

        // Contract worker cannot mutate schedules, only list them.
        var setErr = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(kernel, workerCaller, KernelMcpToolNames.ScheduleSet,
                JsonSerializer.SerializeToElement(new
                {
                    title = "Denied",
                    prompt = "Should fail",
                    runAtMs = cluster.TimeProvider.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds()
                })));
        Assert.Equal(KernelErrorCode.Forbidden, setErr.Code);

        var rmErr = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(kernel, workerCaller, KernelMcpToolNames.ScheduleRm,
                JsonSerializer.SerializeToElement(new { scheduleId = "sch_x" })));
        Assert.Equal(KernelErrorCode.Forbidden, rmErr.Code);

        // Lead schedules one-shot reminder 30 seconds from now and a recurring every-minute cron.
        var runAtIso = cluster.TimeProvider.GetUtcNow().AddSeconds(30).ToString("O");
        var onceJson = await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new
            {
                id = "sch_once_1",
                title = "Follow up release",
                prompt = "REMINDER_FOLLOW_UP_RELEASE",
                runAt = runAtIso
            }));
        Assert.Equal("once", onceJson.GetProperty("scheduleKind").GetString());

        var cronJson = await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new
            {
                id = "sch_cron_1",
                title = "Minute check",
                prompt = "CRON_MINUTE_CHECK",
                cron = "* * * * *",
                timezone = "UTC"
            }));
        Assert.Equal("cron", cronJson.GetProperty("scheduleKind").GetString());

        var listed = await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, workerCaller, KernelMcpToolNames.ScheduleLs,
            JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(2, listed.GetProperty("schedules").GetArrayLength());

        // Nothing due yet.
        var scheduler = cluster.Services.GetRequiredService<CronosScheduleHostedService>();
        Assert.Equal(0, await scheduler.EvaluateDueSchedulesOnceAsync());
        Assert.Empty(cluster.AgentRuntime.ExecutedTurns);

        // Advance 65 seconds: both one-shot and recurring cron fire once cleanly.
        cluster.TimeProvider.Advance(TimeSpan.FromSeconds(65));
        var fired = await scheduler.EvaluateDueSchedulesOnceAsync();
        Assert.Equal(2, fired);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);

        Assert.Contains(cluster.AgentRuntime.ExecutedTurns,
            turn => turn.Input.Prompt.Contains("REMINDER_FOLLOW_UP_RELEASE", StringComparison.Ordinal));
        Assert.Contains(cluster.AgentRuntime.ExecutedTurns,
            turn => turn.Input.Prompt.Contains("CRON_MINUTE_CHECK", StringComparison.Ordinal));

        // Cancel recurring cron and confirm it no longer appears active.
        var rm = await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleRm,
            JsonSerializer.SerializeToElement(new { scheduleId = "sch_cron_1" }));
        Assert.True(rm.GetProperty("canceled").GetBoolean());

        var activeAfter = await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleLs,
            JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(0, activeAfter.GetProperty("schedules").GetArrayLength());
    }

    [Fact]
    public async Task DurableCronScheduleSurvivesHostRestartAndCatchesUpOnceWithoutStorming()
    {
        var dir = Directory.CreateTempSubdirectory("perga-sched-restart-");
        try
        {
            var t0 = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
            string trackId;
            await using (var first = new PergaHostTestCluster(dir.FullName, opts =>
            {
                opts.EnableScheduleService = false;
                opts.EnableMaintenanceService = false;
            }))
            {
                first.TimeProvider.SetUtcNow(t0);
                var kernel = first.Services.GetRequiredService<IPergaKernel>();
                var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Restart", DefaultCwd: first.DataDir));
                var snap = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Persistent", Cwd: first.DataDir));
                trackId = snap.Track.Id;
                await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
                var leadCaller = new McpSessionPrincipal("lead-tok", trackId, snap.Track.LeadSessionId!, true, null, null, []);
                await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleSet,
                    JsonSerializer.SerializeToElement(new
                    {
                        id = "sch_restart",
                        title = "Every minute",
                        prompt = "PERSISTENT_CRON_PROMPT",
                        cron = "* * * * *",
                        timezone = "UTC"
                    }));
            }

            // Reopen after 10 minutes of downtime: must fire once (not 10 times) and advance past t0+10m.
            await using var reopened = new PergaHostTestCluster(dir.FullName, opts =>
            {
                opts.EnableScheduleService = false;
                opts.EnableMaintenanceService = false;
            });
            reopened.TimeProvider.SetUtcNow(t0.AddMinutes(10));
            var scheduler = reopened.Services.GetRequiredService<CronosScheduleHostedService>();
            var kernel2 = reopened.Services.GetRequiredService<IPergaKernel>();

            Assert.Equal(1, await scheduler.EvaluateDueSchedulesOnceAsync());
            await kernel2.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
            Assert.Single(reopened.AgentRuntime.ExecutedTurns,
                t => t.Input.Prompt.Contains("PERSISTENT_CRON_PROMPT", StringComparison.Ordinal));

            // Second immediate evaluation must not fire again until next minute.
            Assert.Equal(0, await scheduler.EvaluateDueSchedulesOnceAsync());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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
        var leadCaller = new McpSessionPrincipal("lead-tok", workTrack.Track.Id, workTrack.Track.LeadSessionId!, true, null, null, []);
        var day0 = DateOnly.ParseExact(todayDate, "yyyy-MM-dd");
        var targetLocalDate = day0.AddDays(1).ToString("yyyy-MM-dd");
        var day2Date = day0.AddDays(2).ToString("yyyy-MM-dd");
        var day3Date = day0.AddDays(3).ToString("yyyy-MM-dd");
        var hostZone = TodayCalendar.Zone(host.TimeProvider);
        var (day1StartMs, _) = TodayCalendar.Bounds(targetLocalDate, hostZone);
        var day1AfternoonMs = DateTimeOffset.FromUnixTimeMilliseconds(day1StartMs).AddHours(14).ToUnixTimeMilliseconds();
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new
            {
                id = "sch_tomorrow",
                title = "Release check-in",
                prompt = "Check release metrics",
                runAtMs = day1AfternoonMs
            }));

        // Also schedule a daily 09:00 cron (maxOccurrences = 2) and fire it once to verify:
        // (1) multi-day expansion across the window, (2) already-fired today occurrence stays visible,
        // and (3) expansion stops once maxOccurrences is exhausted.
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(day1StartMs).AddHours(8));
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new
            {
                id = "daily-standup",
                title = "Daily standup",
                prompt = "Summarize progress",
                cron = "0 9 * * *",
                timezone = TodayCalendar.IanaId(hostZone),
                maxOccurrences = 2
            }));
        // Fire day1 09:00 and view calendar at day1 10:00.
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(day1StartMs).AddHours(10));
        var scheduler = host.Services.GetRequiredService<CronosScheduleHostedService>();
        Assert.Equal(1, await scheduler.EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(workTrack.Track.Id).WaitAsync(Deadline);

        // Also test a mid-day created multi-fire cron ("0 9,15 * * *" created at 10:00 -> upcoming at 15:00 today)
        // and a high-frequency cron with maxOccurrences > 2048 so maxStepBudget does not truncate Day 3.
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new
            {
                id = "midday-multi",
                title = "Twice daily",
                prompt = "Check queue",
                cron = "0 9,15 * * *",
                timezone = TodayCalendar.IanaId(hostZone)
            }));
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new
            {
                id = "every-minute-budget",
                title = "Heartbeat",
                prompt = "Ping",
                cron = "* * * * *",
                timezone = TodayCalendar.IanaId(hostZone),
                maxOccurrences = 5000
            }));

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
        var standups = items.Where(i => i.GetProperty("id").GetString() == "daily-standup").ToArray();
        Assert.Equal(2, standups.Length);
        Assert.Equal(targetLocalDate, standups[0].GetProperty("date").GetString());
        Assert.Equal(day2Date, standups[1].GetProperty("date").GetString());

        // Mid-day created multi-fire cron must appear on day1 (at 15:00), day2, and day3.
        var middayDates = items.Where(i => i.GetProperty("id").GetString() == "midday-multi")
            .Select(i => i.GetProperty("date").GetString()!).ToArray();
        Assert.Equal([targetLocalDate, day2Date, day3Date], middayDates);

        // High-frequency cron with 5000 remaining minutes (~3.47 days) must NOT be truncated before day3 by maxStepBudget.
        var heartbeatDates = items.Where(i => i.GetProperty("id").GetString() == "every-minute-budget")
            .Select(i => i.GetProperty("date").GetString()!).ToArray();
        Assert.Equal([targetLocalDate, day2Date, day3Date], heartbeatDates);

        var checkIn = Assert.Single(items, i => i.GetProperty("id").GetString() == "sch_tomorrow");
        Assert.Equal(targetLocalDate, checkIn.GetProperty("date").GetString());
        Assert.Equal("Release check-in", checkIn.GetProperty("title").GetString());
        Assert.Equal("Release Track", checkIn.GetProperty("track_title").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/today/calendar?start=2026-11-20&end=2026-11-19")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/today/calendar?start=2026-01-01&end=2026-04-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/today/calendar?start=invalid&end=2026-11-20")).StatusCode);
    }
}
