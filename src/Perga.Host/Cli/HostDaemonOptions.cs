namespace Perga.Host.Cli;

internal sealed record HostDaemonOptions(string[] HostArgs, bool Foreground, bool NoOpen, bool Verbose)
{
    public static HostDaemonOptions Parse(string[] args) => new(
        args.Where(arg => arg is not ("--foreground" or "-f" or "--no-open" or "--verbose")).ToArray(),
        args.Any(arg => arg is "--foreground" or "-f"),
        args.Contains("--no-open"),
        args.Contains("--verbose"));

    public static bool IsHelp(string[] args) => args.Any(arg => arg is "-h" or "--help");

    private static bool IsUrlsFlag(string arg) =>
        arg.Equals("--urls", StringComparison.OrdinalIgnoreCase) ||
        arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase);

    public static string? RequestedUrls(string[] args)
    {
        string? urls = null;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (IsUrlsFlag(arg))
                urls = arg.Length > 6 ? arg[7..] : ++index < args.Length ? args[index] : null;
        }
        return urls;
    }

    public HostDaemonOptions WithPreviousArguments(string[] previous)
    {
        if (!HostArgs.Any(IsUrlsFlag))
            return this with { HostArgs = [.. previous, .. HostArgs] };

        var preserved = new List<string>();
        for (var index = 0; index < previous.Length; index++)
        {
            if (!IsUrlsFlag(previous[index]))
                preserved.Add(previous[index]);
            else if (previous[index].Length == 6)
                index++;
        }
        return this with { HostArgs = [.. preserved, .. HostArgs] };
    }

    // The ASP.NET command-line provider takes key/value pairs. Preserve the original
    // standalone switch at the process and state boundary.
    public static string[] ConfigurationArguments(string[] args) =>
        args.Select(arg => arg == "--require-auth" ? "--require-auth=true" : arg).ToArray();
}
