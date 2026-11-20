using System.Globalization;
using System.Text.RegularExpressions;
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
        var dayBounds = dates.Select(d => (Date: d, Bounds: TodayCalendar.Bounds(d, zone))).ToArray();
        var windowStartMs = dayBounds[0].Bounds.Start;
        var windowEndMs = dayBounds[^1].Bounds.End;
        var rawSchedules = await store.ListActiveSchedulesInWindowAsync(windowStartMs, windowEndMs, ct).ConfigureAwait(false);
        var expanded = ExpandSchedulesForCalendar(rawSchedules, dayBounds, windowStartMs, windowEndMs, zone, _scheduleCronEvaluator);

        var days = dates.Select(d =>
        {
            presence.TryGetValue(d, out var p);
            return new TodayCalendarDayDto(d, p?.HasDailyTrack ?? false, p?.HasReport ?? false);
        }).ToArray();

        return new TodayCalendarDto(today, TodayCalendar.IanaId(zone), startDate, endDate, days, expanded);
    }

    private static List<TodayScheduleItemDto> ExpandSchedulesForCalendar(
        IReadOnlyList<TodayScheduleEntry> rawSchedules,
        (string Date, (long Start, long End) Bounds)[] dayBounds,
        long windowStartMs,
        long windowEndMs,
        TimeZoneInfo zone,
        IScheduleCronEvaluator? cronEvaluator)
    {
        var expanded = new List<TodayScheduleItemDto>(rawSchedules.Count);

        foreach (var item in rawSchedules)
        {
            if (item.ScheduleKind != TrackScheduleKinds.Cron ||
                string.IsNullOrWhiteSpace(item.CronExpr) ||
                cronEvaluator is null)
            {
                if (item.NextFireAtMs >= windowStartMs && item.NextFireAtMs < windowEndMs)
                {
                    var day = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(item.NextFireAtMs), zone)
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    expanded.Add(new TodayScheduleItemDto(
                        item.Id, day, item.TrackId, item.TrackTitle, item.Title,
                        item.ScheduleKind, item.CronExpr, item.Timezone, item.NextFireAtMs, item.State));
                }

                continue;
            }

            if (item.MaxOccurrences is { } maxOcc && maxOcc - item.FiredCount <= 0)
                continue;

            var lastAllowedFutureMs = ComputeLastAllowedOccurrenceMs(item, windowEndMs, cronEvaluator);
            foreach (var (d, (dayStartMs, dayEndMs)) in dayBounds)
            {
                var matchedFireMs = ResolveCronOccurrenceForDay(item, dayStartMs, dayEndMs, lastAllowedFutureMs, cronEvaluator);
                if (matchedFireMs.HasValue)
                {
                    expanded.Add(new TodayScheduleItemDto(
                        item.Id, d, item.TrackId, item.TrackTitle, item.Title,
                        item.ScheduleKind, item.CronExpr, item.Timezone, matchedFireMs.Value, item.State));
                }
            }
        }

        expanded.Sort((a, b) =>
        {
            var cmp = a.NextFireAtMs.CompareTo(b.NextFireAtMs);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TrackId, b.TrackId);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });
        return expanded;
    }

    private static long? ComputeLastAllowedOccurrenceMs(
        TodayScheduleEntry item,
        long windowEndMs,
        IScheduleCronEvaluator cronEvaluator)
    {
        if (item.MaxOccurrences is not { } maxOcc)
            return null;

        var remaining = maxOcc - item.FiredCount;
        var cursorMs = item.NextFireAtMs;
        const int maxStepBudget = 2048;
        var steps = 1;
        var exhaustedCron = false;
        while (steps < remaining && steps < maxStepBudget && cursorMs < windowEndMs)
        {
            var nextMs = cronEvaluator.GetNextOccurrenceMs(item.CronExpr!, item.Timezone, cursorMs);
            if (!nextMs.HasValue)
            {
                exhaustedCron = true;
                break;
            }
            cursorMs = nextMs.Value;
            steps++;
        }

        return steps >= remaining || exhaustedCron ? cursorMs : null;
    }

    private static long? ResolveCronOccurrenceForDay(
        TodayScheduleEntry item,
        long dayStartMs,
        long dayEndMs,
        long? lastAllowedFutureMs,
        IScheduleCronEvaluator cronEvaluator)
    {
        if (item.NextFireAtMs >= dayStartMs && item.NextFireAtMs < dayEndMs)
            return item.NextFireAtMs;

        if (dayEndMs <= item.NextFireAtMs)
        {
            if (item.LastFiredAtMs is not { } lastFired || lastFired < dayStartMs)
                return null;
            var searchAfter = Math.Max(dayStartMs, item.CreatedAtMs) - 1;
            if (searchAfter >= Math.Min(dayEndMs, lastFired))
                return null;
            var pastOcc = cronEvaluator.GetNextOccurrenceMs(item.CronExpr!, item.Timezone, searchAfter);
            return pastOcc is { } pastFire && pastFire < dayEndMs && pastFire <= lastFired ? pastFire : null;
        }

        var futureOcc = cronEvaluator.GetNextOccurrenceMs(item.CronExpr!, item.Timezone, dayStartMs - 1);
        return futureOcc is { } futureFire && futureFire < dayEndMs &&
               (!lastAllowedFutureMs.HasValue || futureFire <= lastAllowedFutureMs.Value)
            ? futureFire
            : null;
    }
}

public static partial class TodayCalendar
{
    [GeneratedRegex(@"(?:[Tt ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)(?:[Zz]|[+-]\d{2}(?::?\d{2})?)$")]
    private static partial Regex ExplicitIsoOffsetSuffix();

    public static TimeZoneInfo Zone(TimeProvider time) => time.LocalTimeZone;
    public static string IanaId(TimeZoneInfo zone) =>
        zone == TimeZoneInfo.Utc || string.Equals(zone.Id, "UTC", StringComparison.OrdinalIgnoreCase)
            ? "Etc/UTC"
            : zone.HasIanaId ? zone.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var id) ? id : "Etc/UTC";

    public static bool TryResolveZone(string? timezone, out TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }

        var trimmed = timezone.Trim();
        if (string.Equals(trimmed, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    public static TimeZoneInfo ResolveZone(string timezone) =>
        TryResolveZone(timezone, out var zone)
            ? zone
            : throw KernelException.InvalidArgument($"Unknown timezone '{timezone}'.");

    public static long ParseRunAtMs(string rawRunAt, string? timezone, TimeProvider timeProvider)
    {
        var trimmed = rawRunAt.Trim();
        if (trimmed.Length == 0)
            throw KernelException.InvalidArgument("'runAt' must be a non-empty ISO-8601 timestamp string.");

        if (ExplicitIsoOffsetSuffix().IsMatch(trimmed))
        {
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                return dto.ToUnixTimeMilliseconds();
            throw KernelException.InvalidArgument($"Invalid 'runAt' timestamp '{rawRunAt}'. Expected ISO-8601 date/time.");
        }

        if (!DateTime.TryParse(trimmed, CultureInfo.InvariantCulture,
                DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.RoundtripKind, out var localDt) ||
            localDt.Year < 1970)
        {
            throw KernelException.InvalidArgument($"Invalid 'runAt' timestamp '{rawRunAt}'. Expected ISO-8601 date/time.");
        }

        // If .NET detected an explicit offset or zone specifier outside ExplicitIsoOffsetSuffix (e.g.
        // space before offset or RFC1123 GMT), honor that offset directly instead of re-interpreting it.
        if (localDt.Kind != DateTimeKind.Unspecified)
        {
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var explicitDto))
                return explicitDto.ToUnixTimeMilliseconds();
            throw KernelException.InvalidArgument($"Invalid 'runAt' timestamp '{rawRunAt}'. Expected ISO-8601 date/time.");
        }

        var tz = string.IsNullOrWhiteSpace(timezone) ? Zone(timeProvider) : ResolveZone(timezone);
        var unspecified = DateTime.SpecifyKind(localDt, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified))
            throw KernelException.InvalidArgument($"Timestamp '{rawRunAt}' falls in an invalid local time gap in '{tz.Id}'.");
        var offset = tz.IsAmbiguousTime(unspecified)
            ? tz.GetAmbiguousTimeOffsets(unspecified).Max()
            : tz.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUnixTimeMilliseconds();
    }

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
