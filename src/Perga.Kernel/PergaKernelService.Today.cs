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
        if (endDay == DateOnly.MaxValue)
            throw KernelException.InvalidArgument("end is outside the supported range.");
        if (endDay < startDay)
            throw KernelException.InvalidArgument("end must be on or after start.");
        if (endDay.DayNumber - startDay.DayNumber >= 62)
            throw KernelException.InvalidArgument("Calendar range cannot exceed 62 days.");

        var zone = TodayCalendar.Zone(_timeProvider);
        var now = _timeProvider.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var today = TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dates = new List<string>(endDay.DayNumber - startDay.DayNumber + 1);
        for (var cursor = startDay; cursor <= endDay; cursor = cursor.AddDays(1))
            dates.Add(cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var store = (ITodayReadStore)_store;
        var presence = (await store.ListDailyTrackPresenceAsync(dates, ct).ConfigureAwait(false))
            .ToDictionary(p => p.Date, StringComparer.Ordinal);
        var dayBounds = dates.Select(d => (Date: d, Bounds: TodayCalendar.Bounds(d, zone))).ToArray();
        var windowStartMs = dayBounds[0].Bounds.Start;
        var windowEndMs = dayBounds[^1].Bounds.End;
        var rawSchedules = await store.ListCalendarSchedulesInWindowAsync(windowStartMs, windowEndMs, ct).ConfigureAwait(false);
        var expanded = ExpandSchedulesForCalendar(rawSchedules, dayBounds, windowStartMs, windowEndMs, nowMs, zone, _scheduleCronEvaluator);

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
        long nowMs,
        TimeZoneInfo zone,
        IScheduleCronEvaluator? cronEvaluator)
    {
        var expanded = new List<TodayScheduleItemDto>(rawSchedules.Count);

        foreach (var item in rawSchedules)
        {
            var upcomingDays = new HashSet<string>(StringComparer.Ordinal);
            void Add(string day, long fireMs)
            {
                expanded.Add(new TodayScheduleItemDto(
                    item.Id, day, item.TrackId, item.TrackTitle, item.Title,
                    item.ScheduleKind, item.CronExpr, item.Timezone, fireMs, item.State));
            }
            if (item.State == TrackScheduleStates.Active && item.NextFireAtMs is { } nextFire)
            {
                if (item.ScheduleKind == TrackScheduleKinds.Cron &&
                    !string.IsNullOrWhiteSpace(item.CronExpr) && cronEvaluator is not null)
                {
                    if (item.MaxOccurrences is not { } maxOcc || maxOcc > item.FiredCount)
                    {
                        var lastAllowed = ComputeLastAllowedOccurrenceMs(item, windowEndMs, nowMs, cronEvaluator);
                        foreach (var (day, (start, end)) in dayBounds)
                        {
                            var fire = ResolveCronOccurrenceForDay(item, start, end, nowMs, lastAllowed, cronEvaluator);
                            if (fire is { } fireMs)
                            {
                                Add(day, fireMs);
                                upcomingDays.Add(day);
                            }
                        }
                    }
                }
                else if (nextFire >= windowStartMs && nextFire < windowEndMs)
                {
                    var day = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(nextFire), zone)
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    Add(day, nextFire);
                    upcomingDays.Add(day);
                }
            }
            if (item.LastFiredAtMs is { } fired && fired >= windowStartMs && fired < windowEndMs)
            {
                var day = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(fired), zone)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!upcomingDays.Contains(day)) Add(day, fired);
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
        long nowMs,
        IScheduleCronEvaluator cronEvaluator)
    {
        if (item.NextFireAtMs is not { } nextFire || nextFire >= windowEndMs ||
            item.MaxOccurrences is not { } maxOcc)
            return null;

        nextFire = Math.Max(nextFire, nowMs);
        var remaining = maxOcc - item.FiredCount;
        if (remaining == 1) return nextFire;
        var cursorMs = nextFire;
        const int maxStepBudget = 100_000;
        var steps = 1;
        while (steps < remaining && steps < maxStepBudget && cursorMs < windowEndMs)
        {
            var nextMs = cronEvaluator.GetNextOccurrenceMs(item.CronExpr!, item.Timezone, cursorMs);
            if (!nextMs.HasValue) return cursorMs;
            cursorMs = nextMs.Value;
            steps++;
        }

        // Only crossing the window proves that no in-window cap is necessary.
        return cursorMs >= windowEndMs ? null : cursorMs;
    }

    private static long? ResolveCronOccurrenceForDay(
        TodayScheduleEntry item,
        long dayStartMs,
        long dayEndMs,
        long nowMs,
        long? lastAllowedFutureMs,
        IScheduleCronEvaluator cronEvaluator)
    {
        if (item.NextFireAtMs is not { } nextFire) return null;
        nextFire = Math.Max(nextFire, nowMs);
        if (nextFire >= dayStartMs && nextFire < dayEndMs)
            return nextFire;
        if (dayEndMs <= nextFire) return null;

        var futureOcc = cronEvaluator.GetNextOccurrenceMs(item.CronExpr!, item.Timezone, dayStartMs - 1);
        return futureOcc is { } futureFire && futureFire < dayEndMs &&
               (!lastAllowedFutureMs.HasValue || futureFire <= lastAllowedFutureMs.Value)
            ? futureFire
            : null;
    }
}

public static class TodayCalendar
{
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

        KernelException InvalidTimestamp() => KernelException.InvalidArgument(
            $"Invalid 'runAt' timestamp '{rawRunAt}'. Expected ISO-8601 date/time.");
        if (trimmed.Length < 10 || !DateOnly.TryParseExact(trimmed[..10], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var datePart))
            throw InvalidTimestamp();

        var localDt = datePart.ToDateTime(TimeOnly.MinValue);
        if (trimmed.Length > 10)
        {
            if (trimmed[10] is not ('T' or 't' or ' ')) throw InvalidTimestamp();
            var timePart = trimmed[11..];
            var offsetStart = timePart.IndexOfAny(['Z', 'z', '+', '-']);
            var clock = offsetStart < 0 ? timePart : timePart[..offsetStart].TrimEnd(' ');
            if (!TimeOnly.TryParseExact(clock, ["HH:mm", "HH:mm:ss", "HH:mm:ss.fFFFFFF"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
                throw InvalidTimestamp();
            localDt = datePart.ToDateTime(parsedTime);
            if (offsetStart >= 0)
            {
                // Normalize only the supported ISO offset forms, then parse the instant directly.
                // Parsing through DateTime would first convert it to the host's local timezone.
                var suffix = timePart[offsetStart..];
                if (suffix is "Z" or "z") suffix = "+00:00";
                else
                {
                    if (suffix.Length == 3) suffix += ":00";
                    else if (suffix.Length == 5) suffix = suffix.Insert(3, ":");
                    if (suffix.Length != 6 || suffix[0] is not ('+' or '-') || suffix[3] != ':' ||
                        !char.IsAsciiDigit(suffix[1]) || !char.IsAsciiDigit(suffix[2]) ||
                        !char.IsAsciiDigit(suffix[4]) || !char.IsAsciiDigit(suffix[5]))
                        throw InvalidTimestamp();
                }
                var normalized = localDt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + suffix;
                if (!DateTimeOffset.TryParseExact(normalized, "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var explicitDto))
                    throw InvalidTimestamp();
                var explicitMs = explicitDto.ToUnixTimeMilliseconds();
                return explicitMs >= 0 ? explicitMs : throw InvalidTimestamp();
            }
        }

        var tz = string.IsNullOrWhiteSpace(timezone) ? Zone(timeProvider) : ResolveZone(timezone);
        var unspecified = DateTime.SpecifyKind(localDt, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified))
            throw KernelException.InvalidArgument($"Timestamp '{rawRunAt}' falls in an invalid local time gap in '{tz.Id}'.");
        var offset = tz.IsAmbiguousTime(unspecified)
            ? tz.GetAmbiguousTimeOffsets(unspecified).Max()
            : tz.GetUtcOffset(unspecified);
        try
        {
            var runAtMs = new DateTimeOffset(unspecified, offset).ToUnixTimeMilliseconds();
            return runAtMs >= 0 ? runAtMs : throw InvalidTimestamp();
        }
        catch (ArgumentException)
        {
            throw InvalidTimestamp();
        }
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
