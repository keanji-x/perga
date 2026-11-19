using System.Diagnostics;
using System.Text.Json;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.SystemUpgrade;

public enum UpgradeSourceKind { Tarball, Repo, Release }
public sealed class UpgradeSourceOptions
{
    public UpgradeSourceKind Kind { get; set; }
    public string Location { get; set; } = "";
}

public interface IUpgradeProcessRunner
{
    Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

public sealed class UpgradeProcessRunner : IUpgradeProcessRunner
{
    internal static readonly string[] EnvironmentKeys = ["HOME", "USER", "LOGNAME", "PATH", "LANG", "LC_ALL",
        "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "DOTNET_ROOT", "CARGO_HOME", "RUSTUP_HOME"];
    public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(arguments[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments.Skip(1))
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment.Clear();
        foreach (var key in UpgradeProcessRunner.EnvironmentKeys)
            if (Environment.GetEnvironmentVariable(key) is { } value)
                start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw KernelException.Conflict("Could not start deployment controller.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw KernelException.Conflict("Deployment controller failed: " + error);
        }
        return output;
    }
}

public sealed class SystemUpgradeController(PergaHostOptions options, IUpgradeProcessRunner runner) : ISystemUpgradeController
{
    private List<string> Command(string operation, string? sourceId)
    {
        if (options.InstallationRoot is null)
        {
            throw KernelException.Conflict("Managed installation root is not configured.");
        }
        var root = Path.GetFullPath(options.InstallationRoot);
        var args = new List<string> { "/usr/bin/python3", Path.Combine(root, "bin", "perga_ctl.py"),
            "--root", root, "--name", options.InstallationName, operation };
        if (sourceId is not null)
        {
            if (!options.UpgradeSources.TryGetValue(sourceId, out var source) || string.IsNullOrWhiteSpace(source.Location))
                throw KernelException.InvalidArgument("Unknown installation-owner upgrade source ID.");
            args.Add(source.Kind switch { UpgradeSourceKind.Tarball => "--from-tarball", UpgradeSourceKind.Repo => "--from-repo",
                UpgradeSourceKind.Release => "--from-release", _ => throw KernelException.InvalidArgument("Invalid source kind.") });
            args.Add(source.Location);
        }
        return args;
    }

    public async Task<JsonElement> CheckAsync(string? sourceId, CancellationToken ct)
    {
        using var result = JsonDocument.Parse(await runner.RunAsync(Command("check", sourceId), ct).ConfigureAwait(false));
        return result.RootElement.Clone();
    }

    public async Task<SystemUpgradeHandoff> ScheduleAsync(bool rollback, string? sourceId, string notifyTrackId, CancellationToken ct)
    {
        if (!rollback && sourceId is null)
        {
            throw KernelException.InvalidArgument("A configured sourceId is required.");
        }
        var args = Command(rollback ? "rollback" : "upgrade", sourceId);
        args.AddRange(["--notify-track-id", notifyTrackId]);
        using var result = JsonDocument.Parse(await runner.RunAsync(args, ct).ConfigureAwait(false));
        return new(result.RootElement.GetProperty("jobId").GetString()!, "queued");
    }
}
