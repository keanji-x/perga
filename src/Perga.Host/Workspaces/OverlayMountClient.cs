using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Perga.Host.Configuration;
using Perga.VcsKit;

namespace Perga.Host.Workspaces;

internal sealed class OverlayMountClient : IOverlayMounts, IDisposable
{
    private const string ReadDescriptor = "PERGA_OVERLAYFS_READ_FD";
    private const string WriteDescriptor = "PERGA_OVERLAYFS_WRITE_FD";
    private const string LockDescriptor = "PERGA_OVERLAYFS_LOCK_FD";
    private const string LauncherRoot = "PERGA_OVERLAYFS_ROOT";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SafeFileHandle? _launcherLock;
    private readonly StreamReader? _reader;
    private readonly StreamWriter? _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<Task<bool>> _probe;
    private volatile bool _broken;

    public OverlayMountClient(PergaHostOptions options, string workspacesRoot, bool ephemeral)
    {
        _probe = new Lazy<Task<bool>>(ProbeCoreAsync);
        if (!OperatingSystem.IsLinux())
            return;

        var read = TakeDescriptor(ReadDescriptor);
        var write = TakeDescriptor(WriteDescriptor);
        _launcherLock = TakeDescriptor(LockDescriptor);
        var root = Environment.GetEnvironmentVariable(LauncherRoot);
        Environment.SetEnvironmentVariable(LauncherRoot, null);
        if (read is null || write is null || _launcherLock is null || ephemeral ||
            Environment.OSVersion.Version < new Version(5, 11) ||
            !options.AutoManageTerminalBridge || !string.IsNullOrWhiteSpace(options.TerminalBridgeEndpoint) ||
            root != WorkspaceFileReader.ResolveCanonicalPath(workspacesRoot) ||
            Prctl(4, 0, 0, 0, 0) < 0) // PR_SET_DUMPABLE protects the inherited pipes through /proc.
        {
            read?.Dispose();
            write?.Dispose();
            return;
        }

        _reader = new StreamReader(new FileStream(read, FileAccess.Read));
        _writer = new StreamWriter(new FileStream(write, FileAccess.Write), new UTF8Encoding(false)) { NewLine = "\n" };
    }

    public async Task<bool> ProbeAsync(CancellationToken ct) =>
        await _probe.Value.WaitAsync(ct).ConfigureAwait(false) && !_broken;

    public Task MountAsync(string trackId, string snapshotId, CancellationToken ct) =>
        InvokeAsync("mount", trackId, snapshotId, ct);

    public Task UnmountAsync(string trackId, string snapshotId, CancellationToken ct) =>
        InvokeAsync("unmount", trackId, snapshotId, ct);

    private async Task<bool> ProbeCoreAsync()
    {
        try
        {
            await SendAsync(new MountRequest("probe", null, null), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (WorkspaceUnavailableException)
        {
            return false;
        }
    }

    private async Task InvokeAsync(string operation, string trackId, string snapshotId, CancellationToken ct)
    {
        if (!await ProbeAsync(ct).ConfigureAwait(false))
            throw new WorkspaceUnavailableException("The Linux OverlayFS launcher is unavailable for this Host.");
        await SendAsync(new MountRequest(operation, trackId, snapshotId), ct).ConfigureAwait(false);
    }

    private async Task SendAsync(MountRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_reader is null || _writer is null || _broken)
                throw new WorkspaceUnavailableException("The Linux OverlayFS launcher is unavailable for this Host.");

            try
            {
                ct.ThrowIfCancellationRequested();
                await _writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
                // Once issued, drain this reply even if the caller is canceled so the pipe stays framed.
                var reply = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                var response = reply is null ? null : JsonSerializer.Deserialize<MountReply>(reply, JsonOptions);
                if (response is null)
                    throw new IOException("The launcher closed its reply pipe.");
                if (!response.Ok)
                    throw new WorkspaceUnavailableException(response.Error ?? "The OverlayFS mount operation failed.");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException)
            {
                _broken = true;
                throw new WorkspaceUnavailableException("The OverlayFS launcher stopped responding. Restart the Host with the launcher.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SafeFileHandle? TakeDescriptor(string variable)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        // F_SETFD, FD_CLOEXEC. Agent and terminal subprocesses must not inherit the mount channel.
        if (!int.TryParse(raw, out var descriptor) || descriptor < 3 || Fcntl(descriptor, 2, 1) < 0)
            return null;
        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _launcherLock?.Dispose();
        _gate.Dispose();
    }

    private sealed record MountRequest(string Operation, string? TrackId, string? SnapshotId);
    private sealed record MountReply(bool Ok, string? Error);

    [DllImport("libc", SetLastError = true, EntryPoint = "prctl")]
    private static extern int Prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int Fcntl(int descriptor, int command, int value);
}
