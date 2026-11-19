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
            ContextUsageRatio: 0,
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
    public async Task TodayCalendarAndSchedulesHttpEndpointsReturnPresenceCountsAndValidateInputs()
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
        var leadCaller = new McpSessionPrincipal("lead-tok", workTrack.Track.Id, workTrack.Track.LeadSessionId!, true, null, null, []);
        var targetLocalDate = DateOnly.ParseExact(todayDate, "yyyy-MM-dd").AddDays(1).ToString("yyyy-MM-dd");
        var noonTarget = host.TimeProvider.GetUtcNow().AddDays(1);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, leadCaller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new
            {
                id = "sch_tomorrow",
                title = "Release check-in",
                prompt = "Check release metrics",
                runAtMs = noonTarget.ToUnixTimeMilliseconds()
            }));

        var calResp = await host.Client.GetAsync($"/api/today/calendar?start={todayDate}&end={targetLocalDate}");
        calResp.EnsureSuccessStatusCode();
        using var calDoc = JsonDocument.Parse(await calResp.Content.ReadAsStringAsync());
        Assert.Equal(todayDate, calDoc.RootElement.GetProperty("today").GetString());
        var days = calDoc.RootElement.GetProperty("days");
        Assert.Equal(2, days.GetArrayLength());
        Assert.Equal(todayDate, days[0].GetProperty("date").GetString());
        Assert.True(days[0].GetProperty("has_daily_track").GetBoolean());
        Assert.True(days[0].GetProperty("has_report").GetBoolean());
        Assert.Equal(0, days[0].GetProperty("schedule_count").GetInt32());
        Assert.Equal(targetLocalDate, days[1].GetProperty("date").GetString());
        Assert.False(days[1].GetProperty("has_daily_track").GetBoolean());
        Assert.Equal(1, days[1].GetProperty("schedule_count").GetInt32());

        var schedResp = await host.Client.GetAsync($"/api/today/schedules?date={targetLocalDate}");
        schedResp.EnsureSuccessStatusCode();
        using var schedDoc = JsonDocument.Parse(await schedResp.Content.ReadAsStringAsync());
        var items = schedDoc.RootElement.GetProperty("schedules");
        Assert.Single(items.EnumerateArray());
        Assert.Equal("Release check-in", items[0].GetProperty("title").GetString());
        Assert.Equal("Release Track", items[0].GetProperty("track_title").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/today/calendar?start=2026-11-20&end=2026-11-19")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/today/calendar?start=2026-01-01&end=2026-04-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/today/schedules?date=invalid")).StatusCode);
    }
}
