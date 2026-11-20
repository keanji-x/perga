using Perga.Kernel;

namespace Perga.Kernel.Tests;

public sealed class TodayCalendarTests
{
    [Theory]
    [InlineData("1969-12-31T10:00:00Z")]
    [InlineData("1969-12-31T10:00:00+08:00")]
    [InlineData("10:00:00Z")]
    [InlineData("11-20")]
    [InlineData("November 20")]
    [InlineData("2026-11")]
    [InlineData("2026-02-30")]
    [InlineData("1969-12-31T23:59:59+00:00")]
    [InlineData("1969-12-31T23:59:59")]
    [InlineData("0001-01-01")]
    [InlineData("2026-11-20T10:00:00+08:60")]
    [InlineData("2026-11-20T24:00:00Z")]
    [InlineData("1970-01-01T00:00:00+01:00")]
    [InlineData("2026-11-20junk10:00")]
    [InlineData("2026-11-20T10:00:00.12345678Z")]
    public void RunAtRequiresAnExplicitSupportedDate(string timestamp)
    {
        var error = Assert.Throws<KernelException>(() => TodayCalendar.ParseRunAtMs(timestamp, "UTC", TimeProvider.System));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
    }

    [Theory]
    [InlineData("1970-01-01T00:00:00+00:00", "America/New_York")]
    [InlineData("1970-01-01T00:00:00+00:00", "UTC")]
    [InlineData("1970-01-01t00:00:00z", "Asia/Shanghai")]
    [InlineData("1970-01-01 08:00:00 +08:00", "America/New_York")]
    [InlineData("1970-01-01T08:00+0800", "UTC")]
    [InlineData("1970-01-01T08:00+08", "UTC")]
    public void ExplicitOffsetEpochIsIndependentOfTimezone(string timestamp, string timezone)
    {
        Assert.Equal(0, TodayCalendar.ParseRunAtMs(timestamp, timezone, TimeProvider.System));
    }

    [Theory]
    [InlineData("2026-11-20", "2026-11-20T00:00:00+08:00")]
    [InlineData("2026-11-20t10:00", "2026-11-20T10:00:00+08:00")]
    [InlineData("2026-11-20T10:00:00.1234567", "2026-11-20T10:00:00.1234567+08:00")]
    [InlineData("2026-11-20T10:00:00.1234567Z", "2026-11-20T10:00:00.1234567Z")]
    public void RunAtAcceptsIsoDateAndTimeForms(string timestamp, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected).ToUnixTimeMilliseconds(),
            TodayCalendar.ParseRunAtMs(timestamp, "Asia/Shanghai", TimeProvider.System));
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
