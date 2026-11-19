using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class TrackScheduleTests
{
    [Fact]
    public async Task SetTrackScheduleValidatesInputsAndLeadTarget()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;

        await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest("Bad", "Prompt")));

        await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest("Bad", "Prompt", CronExpr: "* * * * *", RunAtMs: 1000)));

        await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest("Bad", "Prompt", RunAtMs: -1)));

        await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest("Bad", "Prompt", RunAtMs: 1000, Timezone: "Invalid/Zone")));

        await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SetTrackScheduleAsync(trackId, new SetTrackScheduleRequest("Bad", "Prompt", RunAtMs: 1000, TargetSessionId: "ses_other")));

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
    public async Task DynamicScheduleWakeUsesCustomPromptAndTemplateScheduleUsesPolicyPrompt()
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate("""export default defineTemplate({ id: "ops", title: "Ops" });""");
        ctx.PolicyVm.OnBuildScheduleWake = (_, scheduleId, _, state) => new(state, $"POLICY:{scheduleId}");
        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "ops");
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        await ctx.Store.TrackWakes.EnqueueAsync(
            TrackWakeRequests.DynamicSchedule(trackId, "sch_dyn", "CUSTOM_DYNAMIC_PROMPT"),
            default);
        await ctx.Kernel.Actors.GetOrActivate(trackId).PokeLeadAsync(default);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        Assert.Contains(ctx.Store.GetTurnsForSession(leadId),
            t => t.Input.GetProperty("prompt").GetString()!.Contains("CUSTOM_DYNAMIC_PROMPT", StringComparison.Ordinal));

        await ctx.Kernel.TriggerScheduleWakeAsync(trackId, "tpl_sched");
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
        await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.GetTodaySchedulesAsync("2026-02-30"));

        var calendar = await ctx.Kernel.GetTodayCalendarAsync("2026-11-16", "2026-11-18");
        Assert.Equal(3, calendar.Days.Count);
        Assert.Equal("2026-11-16", calendar.Days[0].Date);
        Assert.Equal("2026-11-18", calendar.Days[2].Date);
    }
}
