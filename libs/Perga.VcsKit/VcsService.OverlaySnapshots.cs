using System.Runtime.InteropServices;

namespace Perga.VcsKit;

public sealed partial class VcsService
{
    internal static bool IsInsideRepository(string path) =>
        FindNearestVcsRoot(LocalWorkspaceFileSystemOps.Instance, path, ignoreAmbientRoots: false) is not null;

    internal static bool IsJjOnlySource(string path) =>
        FindNearestVcsRoot(LocalWorkspaceFileSystemOps.Instance, WorkspaceFileReader.ResolveCanonicalPath(path), ignoreAmbientRoots: false) is { HasJj: true, HasGit: false };

    internal async Task<GitSnapshotSource?> ResolveOverlayGitSourceAsync(
        string source, string? baseRevision, CancellationToken ct)
    {
        var root = FindNearestVcsRoot(source, ignoreAmbientRoots: false);
        if (root is { HasJj: true, HasGit: false })
            throw new WorkspaceUnavailableException("Use a Jujutsu workspace for a Jujutsu-only source.");
        if (root is not { HasGit: true })
        {
            if (!string.IsNullOrWhiteSpace(baseRevision))
                throw new WorkspaceUnavailableException("A base revision requires a Git repository.");
            return null;
        }

        var revision = string.IsNullOrWhiteSpace(baseRevision) ? "HEAD" : baseRevision;
        var commit = await RunOverlayGitCheckedAsync(root.Value.RootPath,
            ["rev-parse", "--verify", "--end-of-options", $"{revision}^{{commit}}"], ct).ConfigureAwait(false);
        var explicitPush = await CheckOriginConfigAsync(root.Value.RootPath, ct).ConfigureAwait(false);
        var fetch = await ReadRemoteUrlsAsync(root.Value.RootPath, push: false, ct).ConfigureAwait(false);
        var push = await ReadRemoteUrlsAsync(root.Value.RootPath, push: true, ct).ConfigureAwait(false);
        return new GitSnapshotSource(root.Value.RootPath, commit.Trim(), fetch, push, explicitPush);
    }

    private async Task<bool> CheckOriginConfigAsync(string root, CancellationToken ct)
    {
        var result = await RunOverlayGitAsync(root,
            ["config", "--null", "--get-regexp", @"^(remote\.origin\.(url|pushurl)|url\..*\.(insteadof|pushinsteadof))$"],
            ct).ConfigureAwait(false);
        if (result.ExitCode == 1)
            return false;
        if (result.ExitCode != 0)
            throw new WorkspaceUnavailableException("Git could not read the origin configuration.");

        // get-url separates URLs by newline, so reject values that can masquerade as multiple URLs.
        var records = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Any(record => record.IndexOf('\n') < 0 || record.IndexOf('\n') != record.LastIndexOf('\n')))
            throw new WorkspaceUnavailableException("The OverlayFS Git baseline does not support multiline origin URLs.");
        return records.Any(record => record.StartsWith("remote.origin.pushurl\n", StringComparison.Ordinal));
    }

    private async Task<string[]> ReadRemoteUrlsAsync(string root, bool push, CancellationToken ct)
    {
        string[] args = push ? ["remote", "get-url", "--push", "--all", "origin"] : ["remote", "get-url", "--all", "origin"];
        var result = await RunOverlayGitAsync(root, args, ct).ConfigureAwait(false);
        if (result.ExitCode == 2) // No such remote.
            return [];
        if (result.ExitCode != 0)
            throw new WorkspaceUnavailableException("Git could not read the origin configuration.");
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(url => ResolveLocalGitUrl(url, root))
            .ToArray();
    }

    private static string ResolveLocalGitUrl(string url, string root)
    {
        // Git treats colon syntax as local when a slash precedes the first colon.
        var colon = url.IndexOf(':');
        var slash = url.IndexOf('/');
        if (Path.IsPathRooted(url) || url.StartsWith('~') || (colon >= 0 && (slash < 0 || colon < slash)))
            return url;

        // Resolve symlinks before .., matching Git's filesystem lookup. A relative
        // remote that exists at capture time then outlives the original checkout.
        var path = Path.Combine(root, url);
        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            return path;
        try
        {
            return Marshal.PtrToStringUTF8(resolved) ?? path;
        }
        finally
        {
            Free(resolved);
        }
    }

    internal async Task PrepareOverlayGitSnapshotAsync(GitSnapshotSource source, string target, CancellationToken ct)
    {
        var objectFormat = source.Commit.Length == 64 ? "sha256" : "sha1";
        await RunOverlayGitCheckedAsync(target, ["init", "--quiet", "--template=", $"--object-format={objectFormat}"], ct)
            .ConfigureAwait(false);
        await RunOverlayGitCheckedAsync(target, ["fetch", "--no-tags", "--", source.RepositoryPath, source.Commit], ct)
            .ConfigureAwait(false);
        await RunOverlayGitCheckedAsync(target, ["switch", "--quiet", "--detach", source.Commit], ct)
            .ConfigureAwait(false);
        if (source.FetchUrls.Length > 0)
        {
            await RunOverlayGitCheckedAsync(target, ["remote", "add", "--", "origin", source.FetchUrls[0]], ct)
                .ConfigureAwait(false);
            foreach (var url in source.FetchUrls.Skip(1))
                await RunOverlayGitCheckedAsync(target, ["config", "--add", "remote.origin.url", url], ct).ConfigureAwait(false);
            await CheckOriginConfigAsync(target, ct).ConfigureAwait(false);
            var implicitPush = await ReadRemoteUrlsAsync(target, push: true, ct).ConfigureAwait(false);
            if (source.HasExplicitPushUrl || !implicitPush.SequenceEqual(source.PushUrls))
            {
                foreach (var url in source.PushUrls)
                    await RunOverlayGitCheckedAsync(target, ["config", "--add", "remote.origin.pushurl", url], ct)
                        .ConfigureAwait(false);
            }
        }
    }

    internal async Task ValidateOverlayGitSnapshotAsync(string path, GitSnapshotSource source, CancellationToken ct)
    {
        var configuredWorktree = await RunOverlayGitAsync(path, ["config", "--get", "core.worktree"], ct).ConfigureAwait(false);
        if (configuredWorktree.ExitCode != 1 || File.Exists(Path.Combine(path, ".git", "commondir")) ||
            File.Exists(Path.Combine(path, ".git", "objects", "info", "alternates")))
            throw new WorkspaceUnavailableException("The OverlayFS Git baseline must use standalone Git metadata.");

        await CheckOriginConfigAsync(path, ct).ConfigureAwait(false);
        var fetch = await ReadRemoteUrlsAsync(path, push: false, ct).ConfigureAwait(false);
        var push = await ReadRemoteUrlsAsync(path, push: true, ct).ConfigureAwait(false);
        if (!fetch.SequenceEqual(source.FetchUrls) || !push.SequenceEqual(source.PushUrls))
            throw new WorkspaceUnavailableException("The OverlayFS Git baseline does not preserve the origin configuration.");

        var status = await RunOverlayGitCheckedAsync(path, ["status", "--porcelain=v1", "--untracked-files=no"], ct).ConfigureAwait(false);
        var head = await RunOverlayGitCheckedAsync(path, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status) || head.Trim() != source.Commit)
            throw new WorkspaceUnavailableException("The OverlayFS Git baseline changed during preparation.");
    }

    internal async Task InitializeOverlayBranchAsync(string path, string branch, CancellationToken ct) =>
        await RunOverlayGitCheckedAsync(path, ["switch", "--quiet", "-c", branch], ct).ConfigureAwait(false);

    private Task<ProcessRunResult> RunOverlayGitAsync(string path, IReadOnlyList<string> args, CancellationToken ct) =>
        _processRunner.RunAsync(new ProcessRunRequest(_options.GitExecutablePath,
            ["-c", "core.hooksPath=/dev/null", .. args], path,
            OverlayWorkspaceSnapshots.BuildEnvironment, InheritEnvironment: false), ct);

    private async Task<string> RunOverlayGitCheckedAsync(string path, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await RunOverlayGitAsync(path, args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new WorkspaceUnavailableException($"Git could not prepare the OverlayFS workspace ({args[0]}): {result.StandardError.Trim()}");
        return result.StandardOutput;
    }

    [DllImport("libc", EntryPoint = "realpath")]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolvedPath);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);

    internal sealed record GitSnapshotSource(string RepositoryPath, string Commit, string[] FetchUrls, string[] PushUrls, bool HasExplicitPushUrl);
}
