using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class TrackScheduleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableScheduleWakeFollowsLeadSwitch(bool fireBeforeSwitch)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        var next = (await ctx.Kernel.CreateSessionAsync(id, new CreateSessionRequest(Title: "Next Lead"), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default)).Receipt.Session;
        await ctx.Kernel.SetTrackScheduleAsync(id, new("Switch", "SCHEDULE_SWITCH_PROMPT", RunAtMs: 1));
        if (fireBeforeSwitch) await ctx.Store.TrackSchedules!.FireDueAsync(2, _ => null, default);
        await ctx.Kernel.PatchTrackAsync(id, new PatchTrackRequest(LeadSessionId: next.Id));
        if (!fireBeforeSwitch)
        {
            await ctx.Store.TrackSchedules!.FireDueAsync(2, _ => null, default);
            await ctx.Kernel.Actors.GetOrActivate(id).PokePendingWakesAsync();
        }
        await ctx.Kernel.WaitForTrackIdleAsync(id);
        Assert.Contains(ctx.Store.GetTurnsForSession(next.Id),
            t => t.Input.GetProperty("prompt").GetString()!.Contains("SCHEDULE_SWITCH_PROMPT", StringComparison.Ordinal));
        Assert.Empty(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));
        var wake = Assert.Single(await ctx.Store.TrackWakes.ListAsync(id, default));
        Assert.Null(wake.TargetSessionId);
        Assert.Equal("done", wake.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostCommitPublicationFailurePreservesScheduleResultAndDelivery(bool trigger)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        await ctx.Kernel.SetTrackScheduleAsync(id, new("Reminder", "COMMITTED_PROMPT", Id: "reminder", RunAtMs: 1));
        var faults = 0;
        ctx.Store.BeforeEventPublication = async _ =>
        {
            // Assert the fault is after the durable wake write, and fail only publication.
            Assert.Single(await ctx.Store.TrackWakes.ListAsync(id, default));
            if (faults++ == 0) throw new IOException("post-commit event read failed");
        };
        if (trigger)
            Assert.True(await ctx.Kernel.TriggerTrackScheduleAsync(id, "reminder"));
        else
        {
            var fired = Assert.Single(await ctx.Kernel.FireDueTrackSchedulesAsync(2));
            Assert.Equal((id, "reminder"), (fired.TrackId, fired.ScheduleId));
            Assert.Equal(1, (await ctx.Store.TrackSchedules!.GetAsync(id, "reminder", default))!.FiredCount);
            // The hosted scheduler pokes the returned tracks.
            await ctx.Kernel.Actors.GetOrActivate(fired.TrackId).PokePendingWakesAsync();
        }
        await ctx.Kernel.WaitForTrackIdleAsync(id);
        Assert.True(faults > 0);
        Assert.Contains("COMMITTED_PROMPT", Assert.Single(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!))
            .Input.GetProperty("prompt").GetString());
        Assert.Equal("done", Assert.Single(await ctx.Store.TrackWakes.ListAsync(id, default)).State);
    }

    [Fact]
    public async Task SetTrackScheduleValidatesInputs()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;

        var missingTarget = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest("Bad", "Prompt")));
        Assert.Contains("'cron'", missingTarget.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cronExpr", missingTarget.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest("Bad", "Prompt", CronExpr: "* * * * *", RunAtMs: 1000)));

        await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest("Bad", "Prompt", RunAtMs: -1)));

        await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest("Bad", "Prompt", RunAtMs: 1000, Timezone: "Invalid/Zone")));

        var created = await ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest(
            Title: "Follow-up",
            Prompt: "Check results",
            Id: "sch_valid",
            RunAtMs: ctx.Time.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds(),
            Timezone: "UTC"));

        Assert.Equal("sch_valid", created.Id);
        Assert.Equal(TrackScheduleKinds.Once, created.ScheduleKind);
        Assert.Equal("Etc/UTC", created.Timezone);
        Assert.Equal(TrackScheduleStates.Active, created.State);
    }

    [Fact]
    public async Task AllScheduleSourcesUseAfterPolicy()
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate("""export default defineTemplate({ id: "ops", title: "Ops" });""");
        ctx.PolicyVm.OnScheduleFired = (_, scheduleId, _, state) => new(state, $"POLICY:{scheduleId}");
        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "ops");
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        await ctx.Store.TrackWakes.EnqueueAsync(
            TrackWakeRequests.DynamicSchedule(trackId, "sch_dyn", "CUSTOM_DYNAMIC_PROMPT", "user"),
            default);
        await ctx.Kernel.Actors.GetOrActivate(trackId).PokePendingWakesAsync(default);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        Assert.Contains(ctx.Store.GetTurnsForSession(leadId),
            t => t.Input.GetProperty("prompt").GetString()!.Contains("POLICY:sch_dyn", StringComparison.Ordinal));

        await ctx.Kernel.SetTrackScheduleAsync(trackId, new("Template", "Template prompt", Id: "tpl_sched", RunAtMs: 1));
        await ctx.Kernel.TriggerTrackScheduleAsync(trackId, "tpl_sched");
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        Assert.Contains(ctx.Store.GetTurnsForSession(leadId),
            t => t.Input.GetProperty("prompt").GetString()!.Contains("POLICY:tpl_sched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TodayCalendarValidatesDateBounds()
    {
        await using var ctx = new KernelTestContext();
        await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.GetTodayCalendarAsync("2026-11-20", "2026-11-19"));
        await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.GetTodayCalendarAsync("2026-01-01", "2026-03-04"));
        await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.GetTodayCalendarAsync("2026-02-30", "2026-03-01"));

        var maxDate = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.GetTodayCalendarAsync("9999-12-31", "9999-12-31"));
        Assert.Equal(KernelErrorCode.InvalidArgument, maxDate.Code);

        var calendar = await ctx.Kernel.GetTodayCalendarAsync("2026-11-16", "2026-11-18");
        Assert.Equal(3, calendar.Days.Count);
        Assert.Equal("2026-11-16", calendar.Days[0].Date);
        Assert.Equal("2026-11-18", calendar.Days[2].Date);
        Assert.Empty(calendar.Schedules);
    }

    [Fact]
    public async Task McpRunAtParsingHonorsTimeZoneExplicitSuffixAndArgTypes()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = new McpSessionPrincipal("tok_test", snap.Track.Id, snap.Track.LeadSessionId!, true, null, null, []);

        // Date-only and space/lowercase-'t' offset-less runAt must honor explicit timezone.
        var dateOnlyRes = await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new { title = "Date", prompt = "Go", runAt = "2026-11-20", timezone = "Asia/Shanghai" }));
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T00:00:00+08:00").ToUnixTimeMilliseconds(),
            dateOnlyRes.GetProperty("runAtMs").GetInt64());

        var lowerTRes = await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new { title = "LowerT", prompt = "Go", runAt = "2026-11-20t10:00:00", timezone = "Asia/Shanghai" }));
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:00:00+08:00").ToUnixTimeMilliseconds(),
            lowerTRes.GetProperty("runAtMs").GetInt64());

        // Lowercase trailing 'z' with whitespace keeps UTC instant even when display timezone differs.
        var lowerZRes = await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new { title = "LowerZ", prompt = "Go", runAt = " 2026-11-20T10:00:00z ", timezone = "Asia/Shanghai" }));
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:00:00Z").ToUnixTimeMilliseconds(),
            lowerZRes.GetProperty("runAtMs").GetInt64());

        // Explicit offset outside the compact ISO suffix (space before offset) must still honor its offset.
        var spacedOffsetRes = await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.ScheduleSet,
            JsonSerializer.SerializeToElement(new { title = "SpacedOffset", prompt = "Go", runAt = "2026-11-20 10:00:00 +08:00", timezone = "America/New_York" }));
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:00:00+08:00").ToUnixTimeMilliseconds(),
            spacedOffsetRes.GetProperty("runAtMs").GetInt64());

        // Non-object JSON arguments fail fast on schedule tools.
        var exNonObj = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.ScheduleList,
                JsonSerializer.SerializeToElement(new[] { "bad" })));
        Assert.Equal(KernelErrorCode.InvalidArgument, exNonObj.Code);

        // Unknown properties (like 'cronExpr' instead of schema's 'cron') fail fast instead of being ignored.
        var exUnknown = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.ScheduleSet,
                JsonSerializer.SerializeToElement(new { title = "Bad", prompt = "Go", cronExpr = "0 9 * * *" })));
        Assert.Equal(KernelErrorCode.InvalidArgument, exUnknown.Code);
        Assert.Contains("Unknown argument 'cronExpr'", exUnknown.Message, StringComparison.Ordinal);

        // Malformed optional parameter types fail fast instead of being ignored.
        var exCron = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.ScheduleSet,
                JsonSerializer.SerializeToElement(new { title = "Bad", prompt = "Go", runAtMs = 1_000_000, cron = 123 })));
        Assert.Equal(KernelErrorCode.InvalidArgument, exCron.Code);

        var exInclude = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.ScheduleList,
                JsonSerializer.SerializeToElement(new { includeTerminal = "yes" })));
        Assert.Equal(KernelErrorCode.InvalidArgument, exInclude.Code);
    }
}
