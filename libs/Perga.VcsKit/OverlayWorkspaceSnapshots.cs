using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Perga.VcsKit;

internal sealed class OverlayWorkspaceSnapshots(VcsService vcs, OverlayWorkspaceOptions options)
{
    private static readonly string[] EnvironmentKeys =
    [
        "PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "TMPDIR", "SSH_AUTH_SOCK",
        "CARGO_HOME", "RUSTUP_HOME", "RUSTC_WRAPPER", "RUSTFLAGS", "CARGO_INCREMENTAL",
        "CC", "CXX", "PKG_CONFIG_PATH"
    ];

    internal static IReadOnlyDictionary<string, string?> BuildEnvironment =>
        EnvironmentKeys.ToDictionary(key => key, Environment.GetEnvironmentVariable, StringComparer.Ordinal);

    private readonly ProcessRunner _runner = new();
    private readonly SemaphoreSlim _prepareLock = new(1, 1);

    public async Task<(string Id, string? Commit)> PrepareAsync(
        string snapshots, string source, WorkspaceProvisionRequest request, CancellationToken ct)
    {
        var git = request.NewEmptyWorkspace ? null :
            await vcs.ResolveOverlayGitSourceAsync(source, request.BaseRevision, ct).ConfigureAwait(false);
        var sourceVersion = git is null && !request.NewEmptyWorkspace
            ? await DirectoryVersionAsync(source, ct).ConfigureAwait(false)
            : null;
        var environment = BuildEnvironment;
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 3,
            source = request.NewEmptyWorkspace ? null : source,
            git,
            sourceVersion,
            options.WarmCommand,
            options.CacheVersion,
            architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture,
            buildEnvironment = string.IsNullOrWhiteSpace(options.WarmCommand) ? null :
                environment.Where(pair => pair.Key != "SSH_AUTH_SOCK"),
        });
        var id = Convert.ToHexStringLower(SHA256.HashData(manifest));
        var snapshot = Path.Combine(snapshots, id);

        await _prepareLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(snapshot))
                return (id, git?.Commit);

            var staging = Path.Combine(snapshots, $".staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                if (git is not null)
                {
                    await vcs.PrepareOverlayGitSnapshotAsync(git, staging, ct).ConfigureAwait(false);
                }
                else if (!request.NewEmptyWorkspace)
                {
                    await CopyDirectoryAsync(source, staging, ct).ConfigureAwait(false);
                    if (sourceVersion != await DirectoryVersionAsync(staging, ct).ConfigureAwait(false))
                        throw new WorkspaceUnavailableException("The source directory changed while it was being copied. Retry the workspace setup.");
                }

                ValidateSnapshotTree(staging, git is not null, ct);
                if (git is not null)
                    await vcs.ValidateOverlayGitSnapshotAsync(staging, git, ct).ConfigureAwait(false);

                if (!request.NewEmptyWorkspace && !string.IsNullOrWhiteSpace(options.WarmCommand))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromMinutes(30));
                    var result = await _runner.RunAsync(new ProcessRunRequest(
                        "/bin/sh", ["-ec", options.WarmCommand], staging, environment, InheritEnvironment: false), timeout.Token)
                        .ConfigureAwait(false);
                    if (result.ExitCode != 0)
                        throw new WorkspaceUnavailableException("The OverlayFS warm command failed.");
                    ValidateSnapshotTree(staging, git is not null, ct);
                    if (git is not null)
                        await vcs.ValidateOverlayGitSnapshotAsync(staging, git, ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                // The entire tree becomes visible at once. Another Host may already have published it.
                try
                {
                    Directory.Move(staging, snapshot);
                }
                catch (IOException) when (Directory.Exists(snapshot))
                {
                }
                return (id, git?.Commit);
            }
            finally
            {
                DeletePrivateTree(staging);
            }
        }
        finally
        {
            _prepareLock.Release();
        }
    }

    internal static void DeletePrivateTree(string path)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(path))
            return;

        var root = new DirectoryInfo(path);
        if (root.LinkTarget is not null)
            throw new InvalidDataException("Managed OverlayFS directories must not be symbolic links.");

        // Preserve the modes of published snapshots. Only private staging and deleted Tracks come here.
        var directories = new Stack<DirectoryInfo>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            File.SetUnixFileMode(directory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var child in directory.EnumerateDirectories())
            {
                if (child.LinkTarget is null)
                    directories.Push(child);
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private async Task CopyDirectoryAsync(string source, string staging, CancellationToken ct)
    {
        var result = await _runner.RunAsync(new ProcessRunRequest(
            "/usr/bin/cp", ["-a", "--reflink=auto", "--no-preserve=ownership", "--", Path.Combine(source, "."), staging],
            source, BuildEnvironment, InheritEnvironment: false), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new WorkspaceUnavailableException($"OverlayFS could not copy the source directory: {result.StandardError.Trim()}");
    }

    private static void ValidateSnapshotTree(string root, bool git, CancellationToken ct)
    {
        var gitPath = Path.Combine(root, ".git");
        if (git && (!Directory.Exists(gitPath) || new DirectoryInfo(gitPath).LinkTarget is not null))
            throw new WorkspaceUnavailableException("The OverlayFS Git baseline must have a standalone .git directory.");
        var names = new byte[65536];
        var pending = new Stack<FileSystemInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var entry))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name is ".git" or ".jj" && !(git && entry.FullName == gitPath))
                throw new WorkspaceUnavailableException("The OverlayFS snapshot contains nested Git or Jujutsu metadata.");

            var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            if (git && isLink && entry.FullName.StartsWith(gitPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new WorkspaceUnavailableException("OverlayFS Git metadata must not contain symbolic links.");

            var length = ListExtendedAttributes(entry.FullName, names, (nuint)names.Length);
            if (length < 0 && Marshal.GetLastPInvokeError() != 95) // EOPNOTSUPP
                throw new WorkspaceUnavailableException("OverlayFS could not inspect snapshot attributes.");
            if (length > 0 && Encoding.UTF8.GetString(names, 0, (int)length).Split('\0')
                    .Any(name => name.StartsWith("user.overlay.", StringComparison.Ordinal)))
                throw new WorkspaceUnavailableException("The source contains attributes reserved for OverlayFS.");

            if (entry is DirectoryInfo directory && !isLink)
            {
                foreach (var child in directory.EnumerateFileSystemInfos())
                    pending.Push(child);
            }
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "llistxattr")]
    private static extern nint ListExtendedAttributes(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] names, nuint size);

    private static async Task<string> DirectoryVersionAsync(string source, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = "/usr/bin/tar",
            WorkingDirectory = source,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment.Clear();
        info.Environment["LC_ALL"] = "C";
        foreach (var arg in new[]
        {
            "--sort=name", "--format=pax", "--numeric-owner", "--owner=0", "--group=0", "--xattrs", "--acls",
            "--pax-option=exthdr.name=%d/PaxHeaders/%f,delete=atime,delete=ctime",
            "-cf", "-", "--", "."
        })
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info) ?? throw new WorkspaceUnavailableException("Could not start tar.");
        try
        {
            var errors = process.StandardError.ReadToEndAsync(ct);
            var bytes = await SHA256.HashDataAsync(process.StandardOutput.BaseStream, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var detail = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(detail))
                throw new WorkspaceUnavailableException($"OverlayFS could not read a consistent directory snapshot: {detail.Trim()}");
            return Convert.ToHexStringLower(bytes);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
