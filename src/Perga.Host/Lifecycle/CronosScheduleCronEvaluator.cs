using Cronos;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.Lifecycle;

public sealed class CronosScheduleCronEvaluator : IScheduleCronEvaluator
{
    public bool TryValidate(string cronExpression, string timezone, out string normalizedTimezone, out string? error)
    {
        normalizedTimezone = "Etc/UTC";
        if (string.IsNullOrWhiteSpace(cronExpression) || !TryParseCron(cronExpression.Trim(), out _))
        {
            error = $"Invalid cron expression '{cronExpression}'. Expected 5-field or 6-field cron.";
            return false;
        }

        if (!TryResolveTimeZoneStrict(timezone, out var tz))
        {
            error = $"Unknown timezone '{timezone}'.";
            return false;
        }

        normalizedTimezone = TodayCalendar.IanaId(tz);
        error = null;
        return true;
    }

    public long? GetNextOccurrenceMs(string cronExpression, string timezone, long afterMs)
    {
        if (!TryParseCron(cronExpression.Trim(), out var expr))
            return null;
        if (!TryResolveTimeZoneStrict(timezone, out var tz))
            return null;

        var afterUtc = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, afterMs)).UtcDateTime;
        var next = expr.GetNextOccurrence(afterUtc, tz, inclusive: false);
        if (!next.HasValue)
            return null;
        var utc = DateTime.SpecifyKind(next.Value, DateTimeKind.Utc);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    internal static bool TryParseCron(string rawCron, out CronExpression expression)
    {
        var parts = rawCron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var format = parts.Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        return CronExpression.TryParse(rawCron, format, out expression!);
    }

    internal static bool TryResolveTimeZoneStrict(string? timezone, out TimeZoneInfo zone)
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
}
