using System.Globalization;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public async Task<DailyTrackDto?> GetDailyTrackAsync(string? date, CancellationToken ct = default)
    {
        var zone = TodayCalendar.Zone(_timeProvider);
        var day = date ?? TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        TodayCalendar.ParseDate(day);
        var store = (ITodayReadStore)_store;
        var id = await store.GetDailyTrackIdAsync(day, ct).ConfigureAwait(false);
        if (id is null && date is null)
        {
            var now = NowMs();
            var area = new AreaDto("sys_daily", "Daily", "gray", null, [], 0, now, now);
            var created = await CreateTrackCoreAsync(new(area.Id, Title: day), ct, null, day, area).ConfigureAwait(false);
            id = created.Track.Id;
        }
        return id is null ? null : new(day, TodayCalendar.IanaId(zone), id);
    }

    public async Task<TodayCalendarDto> GetTodayCalendarAsync(string startDate, string endDate, CancellationToken ct = default)
    {
        var startDay = TodayCalendar.ParseDate(startDate);
        var endDay = TodayCalendar.ParseDate(endDate);
        if (endDay < startDay)
            throw KernelException.InvalidArgument("end must be on or after start.");
        if (endDay.DayNumber - startDay.DayNumber >= 62)
            throw KernelException.InvalidArgument("Calendar range cannot exceed 62 days.");

        var zone = TodayCalendar.Zone(_timeProvider);
        var today = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dates = new List<string>(endDay.DayNumber - startDay.DayNumber + 1);
        for (var cursor = startDay; cursor <= endDay; cursor = cursor.AddDays(1))
            dates.Add(cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var store = (ITodayReadStore)_store;
        var presence = (await store.ListDailyTrackPresenceAsync(dates, ct).ConfigureAwait(false))
            .ToDictionary(p => p.Date, StringComparer.Ordinal);
        var (windowStartMs, _) = TodayCalendar.Bounds(startDate, zone);
        var (_, windowEndMs) = TodayCalendar.Bounds(endDate, zone);
        var schedules = await store.ListActiveSchedulesInWindowAsync(windowStartMs, windowEndMs, ct).ConfigureAwait(false);
        var scheduleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in schedules)
        {
            var day = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(item.NextFireAtMs), zone)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            scheduleCounts[day] = scheduleCounts.TryGetValue(day, out var current) ? current + 1 : 1;
        }

        var days = dates.Select(d =>
        {
            presence.TryGetValue(d, out var p);
            scheduleCounts.TryGetValue(d, out var count);
            return new TodayCalendarDayDto(d, p?.HasDailyTrack ?? false, p?.HasReport ?? false, count);
        }).ToArray();

        return new TodayCalendarDto(today, TodayCalendar.IanaId(zone), startDate, endDate, days);
    }

    public async Task<TodaySchedulesDto> GetTodaySchedulesAsync(string date, CancellationToken ct = default)
    {
        TodayCalendar.ParseDate(date);
        var zone = TodayCalendar.Zone(_timeProvider);
        var (startMs, endMs) = TodayCalendar.Bounds(date, zone);
        var store = (ITodayReadStore)_store;
        var schedules = await store.ListActiveSchedulesInWindowAsync(startMs, endMs, ct).ConfigureAwait(false);
        return new TodaySchedulesDto(date, TodayCalendar.IanaId(zone), schedules);
    }
}

public static class TodayCalendar
{
    public static TimeZoneInfo Zone(TimeProvider time) => time.LocalTimeZone;
    public static string IanaId(TimeZoneInfo zone) => zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var id) ? id : "Etc/UTC";

    public static DateOnly ParseDate(string date) => DateOnly.TryParseExact(date, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && date.Length == 10
        ? parsed : throw KernelException.InvalidArgument("date must be YYYY-MM-DD.");

    public static (long Start, long End) Bounds(string date, TimeZoneInfo zone)
    {
        var day = ParseDate(date);
        if (day == DateOnly.MaxValue) throw KernelException.InvalidArgument("date is outside the supported range.");
        long Boundary(DateOnly value)
        {
            var local = value.ToDateTime(TimeOnly.MinValue);
            // Some IANA zones move clocks at midnight. The day starts at its first valid instant.
            while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
            var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
            return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
        }
        return (Boundary(day), Boundary(day.AddDays(1)));
    }
}
