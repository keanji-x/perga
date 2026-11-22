using System.Globalization;
using System.Text.RegularExpressions;

namespace Perga.Contracts;

public static partial class FlowSyntax
{
    public const string DefaultFlowSpecJson = """{"rev":0,"nodes":[]}""";
    public const string DefaultWatchInterval = "60s";
    public const string DefaultWatchTimeout = "60m";
    public const int MinWatchIntervalSeconds = 5;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:-]{0,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex NodeIdRegex();

    [GeneratedRegex(@"^([1-9][0-9]*)([smh])$", RegexOptions.CultureInvariant)]
    public static partial Regex DurationRegex();

    public static bool TryParseDuration(string? raw, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var match = DurationRegex().Match(raw.Trim());
        if (!match.Success ||
            !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
            amount <= 0)
        {
            return false;
        }

        var unit = match.Groups[2].Value;
        try
        {
            duration = unit switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => default
            };
            return duration > TimeSpan.Zero;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
