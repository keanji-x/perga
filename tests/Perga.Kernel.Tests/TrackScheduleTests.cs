using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class TrackScheduleTests
{
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

}
