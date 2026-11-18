using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Perga.AgentKit;

public sealed record CommandInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public sealed partial class OsSandboxWrapper
{
    private static readonly Version MinimumNativeOverlayVersion = new(0, 9);

    private static readonly string[] LinuxCoreReadOnlyRoots =
    [
        "/usr",
        "/bin",
        "/lib",
        "/etc"
    ];

    private static readonly string[] LinuxOptionalReadOnlyRoots =
    [
        "/sbin",
        "/lib64",
        "/run/systemd/resolve"
    ];

    private static readonly string[] HomeReadOnlySubpaths =
    [
        ".dotnet",
        ".local",
        ".local/share",
        ".cargo",
        ".rustup",
        ".nvm",
        ".npm",
        ".codex",
        ".claude",
        ".claude.json",
        ".config",
        ".gemini",
        ".cache",
        ".opencode"
    ];

    private readonly OsSandboxOptions _options;
    private readonly Lazy<Version?> _detectedBwrapVersion;

    public OsSandboxWrapper(OsSandboxOptions? options = null)
    {
        _options = options ?? new OsSandboxOptions();
        _detectedBwrapVersion = new Lazy<Version?>(ProbeBwrapVersionCore, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public OsSandboxOptions Options => _options;

    public static bool SupportsNativeOverlay(Version? bwrapVersion) =>
        bwrapVersion is not null && bwrapVersion >= MinimumNativeOverlayVersion;

    public Version? GetEffectiveBwrapVersion() =>
        _options.BwrapVersionOverride ?? _detectedBwrapVersion.Value;

    public static Version? TryParseBwrapVersion(string? rawVersionOutput)
    {
        if (string.IsNullOrWhiteSpace(rawVersionOutput))
        {
            return null;
        }

        var match = BwrapVersionRegex().Match(rawVersionOutput);
        if (!match.Success)
        {
            return null;
        }

        return Version.TryParse(match.Groups[1].Value, out var parsed) ? parsed : null;
    }

    public CommandInvocation Wrap(
        CommandInvocation inner,
        OsSandboxMode mode,
        IReadOnlyList<OverlayMountSpec>? overlayMounts = null,
        IReadOnlyList<string>? auxiliaryReadOnlyFiles = null,
        string? writableStateDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (string.IsNullOrWhiteSpace(inner.FileName))
        {
            throw new ArgumentException("Executable file name must not be empty.", nameof(inner));
        }

        if (string.IsNullOrWhiteSpace(inner.WorkingDirectory))
        {
            throw new ArgumentException("WorkingDirectory must not be empty.", nameof(inner));
        }

        var normalizedCwd = Path.GetFullPath(inner.WorkingDirectory);
        ValidateOverlayMounts(overlayMounts, mode);

        if (mode == OsSandboxMode.None)
        {
            return inner with { WorkingDirectory = normalizedCwd };
        }

        if (IsLinuxTarget())
        {
            return WrapLinuxBwrap(inner, normalizedCwd, mode, overlayMounts, auxiliaryReadOnlyFiles, writableStateDirectory);
        }

        if (IsMacOsTarget())
        {
            return WrapMacSandboxExec(inner, normalizedCwd, mode);
        }

        throw new PlatformNotSupportedException(
            $"OS sandbox mode '{mode}' is only supported on Linux (bwrap) and macOS (sandbox-exec).");
    }

    public static string BuildMacSeatbeltProfile(string normalizedCwd, OsSandboxMode mode)
    {
        var sb = new StringBuilder();
        sb.Append("(version 1) ");
        sb.Append("(allow default) ");
        sb.Append("(deny file-write*) ");
        if (mode == OsSandboxMode.WorkspaceWriteBwrap)
        {
            sb.Append("(allow file-write* (subpath \"")
                .Append(EscapeSeatbeltString(normalizedCwd))
                .Append("\")) ");
        }

        sb.Append("(allow file-write* (subpath \"/tmp\") (subpath \"/private/tmp\") (literal \"/dev/null\"))");
        return sb.ToString();
    }

    private CommandInvocation WrapLinuxBwrap(
        CommandInvocation inner,
        string normalizedCwd,
        OsSandboxMode mode,
        IReadOnlyList<OverlayMountSpec>? overlayMounts,
        IReadOnlyList<string>? auxiliaryReadOnlyFiles,
        string? writableStateDirectory)
    {
        var args = new List<string>(64)
        {
            "--die-with-parent"
        };

        foreach (var root in LinuxCoreReadOnlyRoots)
        {
            args.Add("--ro-bind");
            args.Add(root);
            args.Add(root);
        }

        foreach (var optionalRoot in LinuxOptionalReadOnlyRoots)
        {
            args.Add("--ro-bind-try");
            args.Add(optionalRoot);
            args.Add(optionalRoot);
        }

        args.Add("--proc");
        args.Add("/proc");
        args.Add("--dev");
        args.Add("/dev");
        args.Add("--tmpfs");
        args.Add("/tmp");

        var homeDir = ResolveHomeDirectory(inner.Environment);
        string? normalizedHome = null;
        var mountedHomeRoDirs = new List<string>(HomeReadOnlySubpaths.Length);
        if (!string.IsNullOrWhiteSpace(homeDir))
        {
            normalizedHome = Path.GetFullPath(homeDir);
            foreach (var subpath in HomeReadOnlySubpaths)
            {
                var candidate = Path.GetFullPath(Path.Combine(normalizedHome, subpath));
                mountedHomeRoDirs.Add(candidate);
                args.Add("--ro-bind-try");
                args.Add(candidate);
                args.Add(candidate);
            }


        }

        AppendExternalExecutableReadOnlyMounts(args, inner, normalizedCwd, normalizedHome, mountedHomeRoDirs);

        foreach (var extraRo in _options.ExtraReadOnlyPaths)
        {
            if (string.IsNullOrWhiteSpace(extraRo))
            {
                continue;
            }

            var fullExtra = Path.GetFullPath(extraRo);
            args.Add("--ro-bind");
            args.Add(fullExtra);
            args.Add(fullExtra);
        }

        args.Add(mode == OsSandboxMode.WorkspaceWriteBwrap ? "--bind" : "--ro-bind");
        args.Add(normalizedCwd);
        args.Add(normalizedCwd);

        if (writableStateDirectory is not null)
        {
            args.Add("--bind");
            args.Add(writableStateDirectory);
            args.Add(writableStateDirectory);
        }

        if (auxiliaryReadOnlyFiles is not null)
        {
            foreach (var auxFile in auxiliaryReadOnlyFiles)
            {
                if (string.IsNullOrWhiteSpace(auxFile))
                {
                    continue;
                }

                var fullAux = Path.GetFullPath(auxFile);
                args.Add("--ro-bind");
                args.Add(fullAux);
                args.Add(fullAux);
            }
        }

        if (overlayMounts is { Count: > 0 })
        {
            AppendOverlayMountArguments(args, overlayMounts, mode);
        }

        args.Add("--chdir");
        args.Add(normalizedCwd);
        args.Add("--");
        args.Add(inner.FileName);
        foreach (var innerArg in inner.Arguments)
        {
            args.Add(innerArg);
        }

        return new CommandInvocation(
            _options.BwrapExecutablePath,
            args,
            normalizedCwd,
            inner.Environment);
    }

    private void AppendOverlayMountArguments(
        List<string> args,
        IReadOnlyList<OverlayMountSpec> overlayMounts,
        OsSandboxMode mode)
    {
        var bwrapVersion = GetEffectiveBwrapVersion();
        var nativeSupported = SupportsNativeOverlay(bwrapVersion);

        if (!nativeSupported && _options.RequireNativeOverlaySupport)
        {
            var verText = bwrapVersion?.ToString() ?? "unknown";
            throw new NotSupportedException(
                $"bwrap version '{verText}' does not support native --overlay flags (requires >= 0.9.0).");
        }

        foreach (var mount in overlayMounts)
        {
            var targetPath = Path.GetFullPath(mount.TargetPath);

            if (nativeSupported)
            {
                foreach (var lower in mount.LowerDirs)
                {
                    var fullLower = Path.GetFullPath(lower);
                    args.Add("--overlay-src");
                    args.Add(fullLower);
                }

                var writableRequested = mode == OsSandboxMode.WorkspaceWriteBwrap &&
                                        !string.IsNullOrWhiteSpace(mount.UpperDir);

                if (writableRequested)
                {
                    args.Add("--overlay");
                    args.Add(Path.GetFullPath(mount.UpperDir!));
                    args.Add(Path.GetFullPath(mount.WorkDir!));
                    args.Add(targetPath);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(mount.UpperDir))
                    {
                        args.Add("--overlay-src");
                        args.Add(Path.GetFullPath(mount.UpperDir));
                    }

                    args.Add("--ro-overlay");
                    args.Add(targetPath);
                }
            }
            else
            {
                // Clean fallback on bwrap 0.8.x: bind UpperDir when present, otherwise bind top-most LowerDir read-only.
                if (!string.IsNullOrWhiteSpace(mount.UpperDir))
                {
                    var fullUpper = Path.GetFullPath(mount.UpperDir);
                    args.Add(mode == OsSandboxMode.WorkspaceWriteBwrap ? "--bind" : "--ro-bind");
                    args.Add(fullUpper);
                    args.Add(targetPath);
                }
                else
                {
                    var topLower = Path.GetFullPath(mount.LowerDirs[^1]);
                    args.Add("--ro-bind");
                    args.Add(topLower);
                    args.Add(targetPath);
                }
            }
        }
    }

    private CommandInvocation WrapMacSandboxExec(
        CommandInvocation inner,
        string normalizedCwd,
        OsSandboxMode mode)
    {
        var profile = BuildMacSeatbeltProfile(normalizedCwd, mode);
        var args = new List<string>(inner.Arguments.Count + 3)
        {
            "-p",
            profile,
            inner.FileName
        };

        foreach (var arg in inner.Arguments)
        {
            args.Add(arg);
        }

        return new CommandInvocation(
            _options.MacSandboxExecPath,
            args,
            normalizedCwd,
            inner.Environment);
    }

    private static void ValidateOverlayMounts(IReadOnlyList<OverlayMountSpec>? overlayMounts, OsSandboxMode mode)
    {
        if (overlayMounts is null || overlayMounts.Count == 0)
        {
            return;
        }

        for (var i = 0; i < overlayMounts.Count; i++)
        {
            var mount = overlayMounts[i];
            if (mount is null)
            {
                throw new ArgumentException($"OverlayMounts[{i}] must not be null.", nameof(overlayMounts));
            }

            if (mount.LowerDirs is null || mount.LowerDirs.Count == 0 || mount.LowerDirs.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    $"OverlayMounts[{i}] must specify at least one non-empty LowerDir.",
                    nameof(overlayMounts));
            }

            if (string.IsNullOrWhiteSpace(mount.TargetPath))
            {
                throw new ArgumentException(
                    $"OverlayMounts[{i}] must specify a non-empty TargetPath.",
                    nameof(overlayMounts));
            }

            var hasUpper = !string.IsNullOrWhiteSpace(mount.UpperDir);
            var hasWork = !string.IsNullOrWhiteSpace(mount.WorkDir);
            if (hasWork && !hasUpper)
            {
                throw new ArgumentException(
                    $"OverlayMounts[{i}] specifies WorkDir without UpperDir.",
                    nameof(overlayMounts));
            }

            if (mode == OsSandboxMode.WorkspaceWriteBwrap && hasUpper && !hasWork)
            {
                throw new ArgumentException(
                    $"OverlayMounts[{i}] specifies UpperDir in WorkspaceWriteBwrap mode without WorkDir.",
                    nameof(overlayMounts));
            }
        }
    }

    private bool IsLinuxTarget()
    {
        if (_options.PlatformOverride is { } platform)
        {
            return platform == OSPlatform.Linux;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }

    private bool IsMacOsTarget()
    {
        if (_options.PlatformOverride is { } platform)
        {
            return platform == OSPlatform.OSX;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }

    private string? ResolveHomeDirectory(IReadOnlyDictionary<string, string> env)
    {
        if (!string.IsNullOrWhiteSpace(_options.HomeDirectoryOverride))
        {
            return _options.HomeDirectoryOverride;
        }

        if (env.TryGetValue("HOME", out var envHome) && !string.IsNullOrWhiteSpace(envHome))
        {
            return envHome;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? null : profile;
    }

    private Version? ProbeBwrapVersionCore()
    {
        if (!IsLinuxTarget() || !File.Exists(_options.BwrapExecutablePath))
        {
            return null;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _options.BwrapExecutablePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start())
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(2000);
            return TryParseBwrapVersion(string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
        }
        catch
        {
            return null;
        }
    }

    private static void AppendExternalExecutableReadOnlyMounts(
        List<string> args,
        CommandInvocation inner,
        string normalizedCwd,
        string? normalizedHome,
        IReadOnlyList<string> mountedHomeRoDirs)
    {
        var candidateDirs = new List<string>(4);

        string? resolvedExe = null;
        if (inner.FileName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            inner.FileName.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            var fullExe = Path.GetFullPath(inner.FileName);
            resolvedExe = fullExe;
            var dir = Path.GetDirectoryName(fullExe);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                candidateDirs.Add(dir);
            }
        }
        else
        {
            resolvedExe = AgentRuntime.FindExecutableOnPath(inner.FileName, inner.Environment);
            if (!string.IsNullOrWhiteSpace(resolvedExe))
            {
                var dir = Path.GetDirectoryName(resolvedExe);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    candidateDirs.Add(dir);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(resolvedExe) && File.Exists(resolvedExe))
        {
            try
            {
                var linkTarget = File.ResolveLinkTarget(resolvedExe, returnFinalTarget: true);
                if (linkTarget is not null)
                {
                    var realExe = Path.GetFullPath(linkTarget.FullName);
                    var realDir = Path.GetDirectoryName(realExe);
                    if (!string.IsNullOrWhiteSpace(realDir))
                    {
                        candidateDirs.Add(realDir);

                        // If binary lives in <pkg>/bin/<exe> outside /usr,/bin,$HOME, mount <pkg> so ../lib works too.
                        var dirName = Path.GetFileName(realDir.TrimEnd(Path.DirectorySeparatorChar));
                        if (string.Equals(dirName, "bin", StringComparison.OrdinalIgnoreCase))
                        {
                            var parentPkg = Path.GetDirectoryName(realDir);
                            if (!string.IsNullOrWhiteSpace(parentPkg))
                            {
                                candidateDirs.Add(parentPkg);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore symlink resolution errors.
            }
        }

        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawDir in candidateDirs)
        {
            var dir = Path.GetFullPath(rawDir);
            if (!ShouldMountExternalExecutableDir(dir, normalizedCwd, normalizedHome, mountedHomeRoDirs))
            {
                continue;
            }

            if (added.Add(dir))
            {
                args.Add("--ro-bind-try");
                args.Add(dir);
                args.Add(dir);
            }
        }
    }

    private static bool ShouldMountExternalExecutableDir(
        string dir,
        string normalizedCwd,
        string? normalizedHome,
        IReadOnlyList<string> mountedHomeRoDirs)
    {
        var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar);
        if (string.IsNullOrEmpty(trimmed) ||
            string.Equals(trimmed, "/home", StringComparison.Ordinal) ||
            string.Equals(trimmed, "/Users", StringComparison.Ordinal) ||
            string.Equals(trimmed, "/proc", StringComparison.Ordinal) ||
            string.Equals(trimmed, "/dev", StringComparison.Ordinal) ||
            string.Equals(trimmed, "/tmp", StringComparison.Ordinal) ||
            string.Equals(trimmed, "/opt", StringComparison.Ordinal) ||
            string.Equals(trimmed, "/var", StringComparison.Ordinal) ||
            string.Equals(trimmed, "/run", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalizedHome))
        {
            var trimmedHome = normalizedHome.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(trimmed, trimmedHome, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var coreRoot in LinuxCoreReadOnlyRoots)
        {
            if (IsSameOrSubpath(dir, coreRoot))
            {
                return false;
            }
        }

        foreach (var optRoot in LinuxOptionalReadOnlyRoots)
        {
            if (IsSameOrSubpath(dir, optRoot))
            {
                return false;
            }
        }

        if (IsSameOrSubpath(dir, normalizedCwd))
        {
            return false;
        }

        foreach (var homeRo in mountedHomeRoDirs)
        {
            if (IsSameOrSubpath(dir, homeRo))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSameOrSubpath(string candidatePath, string basePath)
    {
        var normCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar);
        var normBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(normCandidate, normBase, StringComparison.Ordinal))
        {
            return true;
        }

        return normCandidate.StartsWith(normBase + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string EscapeSeatbeltString(string path) =>
        path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    [GeneratedRegex(@"bubblewrap\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BwrapVersionRegex();
}
