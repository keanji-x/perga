using Perga.Kernel;

namespace Perga.Kernel.Tests;

public sealed class TodayCalendarTests
{
    [Theory]
    [InlineData("1969-12-31T10:00:00Z")]
    [InlineData("1969-12-31T10:00:00+08:00")]
    [InlineData("10:00:00Z")]
    public void RunAtRequiresAnExplicitSupportedDate(string timestamp)
    {
        var error = Assert.Throws<KernelException>(() => TodayCalendar.ParseRunAtMs(timestamp, "UTC", TimeProvider.System));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public void RunAtRejectsDstGapAndChoosesEarlierAmbiguousInstant()
    {
        Assert.Throws<KernelException>(() => TodayCalendar.ParseRunAtMs("2026-03-08T02:30:00", "America/New_York", TimeProvider.System));
        Assert.Equal(DateTimeOffset.Parse("2026-11-01T01:30:00-04:00").ToUnixTimeMilliseconds(),
            TodayCalendar.ParseRunAtMs("2026-11-01T01:30:00", "America/New_York", TimeProvider.System));
    }

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
