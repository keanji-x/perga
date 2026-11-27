using System.Diagnostics;
using System.Text.Json;

namespace Perga.Host.Cli;

internal sealed record HostDaemonState(
    int Pid,
    long StartedAtUtcTicks,
    string ProcessName,
    string Url,
    string Version,
    string InstallDir,
    string DataDir,
    string DbPath,
    string LogPath,
    string[] HostArgs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static string PathIn(string dataDir) => Path.Combine(dataDir, "host.state.json");
    public static string LogPathIn(string dataDir) => Path.Combine(dataDir, "logs", "host.log");

    public static HostDaemonState? Read(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<HostDaemonState>(stream, Json)
                ?? throw new JsonException("Expected a daemon state object.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException ex) { throw new IOException($"Invalid Perga daemon state in {path}.", ex); }
    }

    public void Write(string path)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, PrivateFileOptions(FileMode.CreateNew)))
                JsonSerializer.Serialize(stream, this, Json);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    internal static FileStreamOptions PrivateFileOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    public void RemoveIfOwned(string path)
    {
        if (Read(path) is { } current && current.Pid == Pid && current.StartedAtUtcTicks == StartedAtUtcTicks)
            File.Delete(path);
    }

    public Process? GetOwnedProcess()
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(Pid);
            var startedAt = process.StartTime.ToUniversalTime().Ticks;
            if (!HasExited(process) &&
                StartedAtUtcTicks >= startedAt - TimeSpan.FromSeconds(5).Ticks &&
                StartedAtUtcTicks <= startedAt + TimeSpan.FromSeconds(5).Ticks &&
                process.ProcessName == ProcessName)
            {
                var owned = process;
                process = null;
                return owned;
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        finally
        {
            process?.Dispose();
        }

        return null;
    }

    public static bool HasExited(Process process)
    {
        if (process.HasExited)
            return true;
        if (!OperatingSystem.IsLinux())
            return false;

        // A container's PID 1 may leave the detached process as a zombie.
        try
        {
            var stat = File.ReadAllText($"/proc/{process.Id}/stat");
            var stateIndex = stat.LastIndexOf(')') + 2;
            return stateIndex < stat.Length && stat[stateIndex] is 'Z' or 'X';
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }
}
