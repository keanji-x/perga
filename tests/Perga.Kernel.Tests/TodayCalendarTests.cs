using Perga.Kernel;

namespace Perga.Kernel.Tests;

public sealed class TodayCalendarTests
{
    [Theory]
    [InlineData("2026-03-08", 23)]
    [InlineData("2026-11-01", 25)]
    public void DayBoundariesFollowIanaDaylightSaving(string date, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var (start, end) = TodayCalendar.Bounds(date, zone);
        Assert.Equal(TimeSpan.FromHours(hours).TotalMilliseconds, end - start);
        Assert.Equal(date, TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(start), zone).ToString("yyyy-MM-dd"));
        Assert.Equal("America/New_York", TodayCalendar.IanaId(zone));
    }
}
