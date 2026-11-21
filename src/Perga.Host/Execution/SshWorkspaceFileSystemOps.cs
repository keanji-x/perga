using System.Collections.Concurrent;
using Perga.VcsKit;

namespace Perga.Host.Execution;

public sealed class SshWorkspaceFileSystemOps : IWorkspaceFileSystemOps
{
    private readonly SshTargetConfig _config;
    private readonly ConcurrentDictionary<string, string> _resolvedExecutableCache = new(StringComparer.Ordinal);

    public SshWorkspaceFileSystemOps(SshTargetConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public bool IsLocal => false;

    public (string RootPath, bool HasJj, bool HasGit)? FindNearestVcsRoot(
        string startDirectory, bool searchAncestors = true, bool ignoreAmbientRoots = true)
    {
        var script = $$"""
            current={{SshCommandBuilder.QuotePosix(Path.TrimEndingDirectorySeparator(Path.GetFullPath(startDirectory)))}}
            while :; do
              has_jj=0
              has_git=0
              if [ {{(ignoreAmbientRoots ? "1" : "0")}} = 0 ] || { [ "$current" != / ] && [ "$current" != /tmp ]; }; then
                if [ -d "$current/.jj" ] && [ -e "$current/.jj/repo" ]; then has_jj=1; fi
                if [ -d "$current/.git" ] && [ -f "$current/.git/HEAD" ]; then has_git=1
                elif [ -f "$current/.git" ] && head -n 1 -- "$current/.git" | grep -qi '^[[:space:]]*gitdir:'; then has_git=1; fi
              fi
              if [ "$has_jj" = 1 ] || [ "$has_git" = 1 ]; then
                printf '%s\n%s\n%s\n' "$has_jj" "$has_git" "$current"
                exit 0
              fi
              [ {{(searchAncestors ? "1" : "0")}} = 1 ] && [ "$current" != / ] || break
              current=$(dirname -- "$current")
            done
            """;
        var (code, stdout, stderr) = RunSync(script);
        if (code != 0)
        {
            throw new IOException($"Failed to find repository on target '{_config.TargetId}': {stderr.Trim()}");
        }

        var parts = stdout.TrimEnd('\n').Split('\n', 3);
        return parts.Length == 3 ? (parts[2], parts[0] == "1", parts[1] == "1") : null;
    }

    public bool DirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var script = $"[ -d {SshCommandBuilder.QuotePosix(path)} ]";
        var (code, _, _) = RunSync(script);
        return code == 0;
    }

    public bool FileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var script = $"[ -f {SshCommandBuilder.QuotePosix(path)} ]";
        var (code, _, _) = RunSync(script);
        return code == 0;
    }

    public string? ReadFirstLine(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var q = SshCommandBuilder.QuotePosix(path);
        var script = $"[ -f {q} ] && head -n 1 -- {q}";
        var (code, stdout, _) = RunSync(script);
        if (code != 0)
        {
            return null;
        }

        return stdout.TrimEnd('\r', '\n');
    }

    public byte[]? ReadContainedFileBytes(string workspaceRoot, string path, int maxBytes)
    {
        const string python = """
            import os, stat, sys
            root = os.path.realpath(sys.argv[1])
            candidate = os.path.realpath(sys.argv[2])
            if os.path.commonpath([root, candidate]) != root:
                sys.exit(1)
            fd = os.open(candidate, os.O_RDONLY | os.O_NONBLOCK)
            with os.fdopen(fd, "rb") as f:
                if not stat.S_ISREG(os.fstat(f.fileno()).st_mode):
                    sys.exit(1)
                sys.stdout.buffer.write(f.read(int(sys.argv[3])))
            """;
        var script = $"python3 -c {SshCommandBuilder.QuotePosix(python)} " +
            $"{SshCommandBuilder.QuotePosix(workspaceRoot)} {SshCommandBuilder.QuotePosix(path)} {Math.Max(1, maxBytes)}";
        var (code, bytes, _) = RunBytesSync(script);
        return code == 0 ? bytes : null;
    }

    public void CreateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var script = $"mkdir -p -- {SshCommandBuilder.QuotePosix(path)}";
        var (code, _, stderr) = RunSync(script);
        if (code != 0)
        {
            throw new IOException($"Failed to create remote directory '{path}' on target '{_config.TargetId}': {stderr.Trim()}");
        }
    }

    public void CopyDirectory(string sourceDir, string targetDir, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);

        const string pyScript = """
            import os, shutil, sys
            src = os.path.abspath(sys.argv[1])
            dst = os.path.abspath(sys.argv[2])
            if not os.path.isdir(src):
                sys.stderr.write(f"Source directory '{src}' does not exist.\n")
                sys.exit(2)
            os.makedirs(dst, exist_ok=True)
            dst_prefix = dst.rstrip(os.sep) + os.sep
            for root, dirs, files in os.walk(src):
                abs_root = os.path.abspath(root)
                if abs_root == dst or abs_root.startswith(dst_prefix):
                    dirs[:] = []
                    continue
                rel_root = os.path.relpath(abs_root, src)
                out_dir = dst if rel_root == "." else os.path.join(dst, rel_root)
                os.makedirs(out_dir, exist_ok=True)
                dirs[:] = [d for d in dirs if not (os.path.abspath(os.path.join(abs_root, d)) == dst or os.path.abspath(os.path.join(abs_root, d)).startswith(dst_prefix))]
                for f in files:
                    src_f = os.path.join(abs_root, f)
                    dst_f = os.path.join(out_dir, f)
                    shutil.copy2(src_f, dst_f)
            """;

        var remoteScript =
            $"python3 -c {SshCommandBuilder.QuotePosix(pyScript)} {SshCommandBuilder.QuotePosix(sourceDir)} {SshCommandBuilder.QuotePosix(targetDir)}";

        var (code, _, stderr) = SshCommandBuilder
            .RunTextAsync(_config, remoteScript, stdin: null, ct)
            .GetAwaiter()
            .GetResult();
        if (code == 2)
        {
            throw new DirectoryNotFoundException($"Source directory '{sourceDir}' does not exist on target '{_config.TargetId}'.");
        }

        if (code != 0)
        {
            throw new IOException($"Failed to copy remote directory '{sourceDir}' to '{targetDir}' on target '{_config.TargetId}': {stderr.Trim()}");
        }
    }

    public void DeleteDirectoryIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var trimmed = path.Trim();
        if (trimmed is "/" or "." or "..")
        {
            throw new InvalidOperationException($"Refusing to recursively delete unsafe path '{trimmed}'.");
        }

        var q = SshCommandBuilder.QuotePosix(trimmed);
        var script = $"if [ -e {q} ]; then chmod -R u+w -- {q} 2>/dev/null || true; rm -rf -- {q}; fi";
        var (code, _, stderr) = RunSync(script);
        if (code != 0)
        {
            throw new IOException($"Failed to delete remote directory '{path}' on target '{_config.TargetId}': {stderr.Trim()}");
        }
    }

    public string? ResolveExecutable(string executableName, string? pathEnvironmentOverride = null)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var cacheKey = $"{executableName}|{pathEnvironmentOverride ?? string.Empty}";
        if (_resolvedExecutableCache.TryGetValue(cacheKey, out var cached))
        {
            return string.IsNullOrEmpty(cached) ? null : cached;
        }

        Dictionary<string, string?>? extraEnv = null;
        if (!string.IsNullOrWhiteSpace(pathEnvironmentOverride))
        {
            extraEnv = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = pathEnvironmentOverride };
        }

        var remoteScript = SshCommandBuilder.BuildRemoteScript(
            workingDirectory: null,
            extraEnv: extraEnv,
            targetDefaultEnv: _config.ExtraEnv,
            body: $"command -v -- {SshCommandBuilder.QuotePosix(executableName)} 2>/dev/null");

        var (code, stdout, _) = RunSync(remoteScript);
        var resolved = code == 0 ? stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() : null;
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            _resolvedExecutableCache[cacheKey] = resolved;
            return resolved;
        }

        return null;
    }

    private (int ExitCode, string Stdout, string Stderr) RunSync(string remoteScript) =>
        SshCommandBuilder.RunTextAsync(_config, remoteScript, stdin: null, CancellationToken.None).GetAwaiter().GetResult();

    private (int ExitCode, byte[] Stdout, string Stderr) RunBytesSync(string remoteScript) =>
        SshCommandBuilder.RunBytesAsync(_config, remoteScript, stdinBytes: null, CancellationToken.None).GetAwaiter().GetResult();
}
