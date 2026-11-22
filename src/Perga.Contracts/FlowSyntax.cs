using System.Globalization;
using System.Text.RegularExpressions;

namespace Perga.Contracts;

public static partial class FlowSyntax
{
    public const string DefaultFlowSpecJson = """{"rev":0,"nodes":[]}""";
    public const string DefaultWatchInterval = "60s";
    public const string DefaultWatchTimeout = "60m";
    public const string NoWatchTimeout = "never";
    public const int MinWatchIntervalSeconds = 5;

    public static string WatchTimeout(FlowDoSpecDto action) =>
        action.Timeout ?? (action.Repeat == true ? NoWatchTimeout : DefaultWatchTimeout);

    public static void ValidateActionOptions(FlowDoSpecDto action, string path, List<FlowValidationErrorDto> errors)
    {
        var hasCommand = !string.IsNullOrWhiteSpace(action.Cmd);
        if (action.Repeat is not null && (action.Kind != "watch" || !hasCommand || action.PrChecks.HasValue))
            errors.Add(new($"{path}.repeat", "'repeat' is only valid for command watches."));
        if (action.ReasoningEffort is { } effort &&
            (action.Kind != "run" || hasCommand || string.IsNullOrWhiteSpace(effort)))
            errors.Add(new($"{path}.reasoningEffort", "'reasoningEffort' must be non-blank and is only valid for agent runs."));
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:-]{0,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex NodeIdRegex();

    [GeneratedRegex(@"^([1-9][0-9]*)([smh])$", RegexOptions.CultureInvariant)]
    public static partial Regex DurationRegex();

    [GeneratedRegex(@"^([1-9][0-9]*)([smhd])$", RegexOptions.CultureInvariant)]
    private static partial Regex WatchTimeoutRegex();

    public static bool TryParseDuration(string? raw, out TimeSpan duration, bool allowDays = false)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var match = (allowDays ? WatchTimeoutRegex() : DurationRegex()).Match(raw.Trim());
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
                "d" => TimeSpan.FromDays(amount),
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
