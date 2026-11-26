using System.ComponentModel;
using System.Text.RegularExpressions;
using Perga.AgentKit;

namespace Perga.Host.Cli;

/// <summary>
/// Installation commands of the <c>perga</c> launcher: <c>version</c>, <c>doctor</c>, <c>upgrade</c>, <c>uninstall</c> and <c>help</c>.
/// </summary>
public static partial class HostCliCommand
{
    public const string DefaultRepo = "keanji-x/perga";
    public const string DefaultInstallerUrl = "https://keanji-x.github.io/perga/install.sh";
    public const string InstallMarker = ".perga-install-marker";

    private static readonly string[] RequiredExecutables = ["Perga.Host", "perga-term-bridge"];
    private static readonly string[] RequiredFiles = ["web/index.html", "oar-bridge/perga-oar-bridge.mjs"];

    private const string Usage = """
        Usage:
          perga [host args...]          Start Perga on http://127.0.0.1:4041 (override with --urls or PERGA_URLS)
          perga start [host args...]    Same as 'perga'
          perga auth <subcommand>       Manage the owner account (see 'perga auth --help')
          perga version                 Print the installed version and paths
          perga doctor [--deep]         Check the package, Node.js 24+, and provider CLIs (--deep probes auth/models)
          perga upgrade [version]       Install the latest release, or a tag such as v0.1.0
          perga uninstall [--yes]       Remove the installed package and launcher; keep user data
        """;

    public static bool Handles(string[] args) => args is [var first, ..] && first is
        "version" or "-v" or "--version" or "doctor" or "upgrade" or "uninstall" or "help" or "-h" or "--help";

    public static async Task<int> RunAsync(string[] args, HostCliEnvironment env, CancellationToken ct = default)
    {
        try
        {
            return args switch
            {
                ["help" or "-h" or "--help", ..] => Help(env),
                ["version" or "-v" or "--version", .. var rest] => Version(env, rest),
                ["doctor", .. var rest] => await DoctorAsync(env, rest, ct).ConfigureAwait(false),
                ["upgrade", .. var rest] => await UpgradeAsync(env, rest, ct).ConfigureAwait(false),
                ["uninstall", .. var rest] => Uninstall(env, rest),
                _ => throw new UsageException($"unknown command '{string.Join(' ', args)}'.", Usage)
            };
        }
        catch (UsageException ex)
        {
            await env.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            await env.Error.WriteLineAsync(ex.Usage).ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex) when (ex is CliException or IOException or InvalidDataException or FormatException or
                                   UnauthorizedAccessException or Win32Exception)
        {
            await env.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static int Help(HostCliEnvironment env)
    {
        env.Output.WriteLine(Usage);
        return 0;
    }

    private static int Version(HostCliEnvironment env, string[] args)
    {
        if (NoArguments(env, args, "Usage: perga version")) return 0;
        var paths = InstallationPaths.From(env);
        env.Output.WriteLine($"perga {ReadVersion(paths.InstallDir) ?? "unknown"}");
        env.Output.WriteLine($"install: {paths.InstallDir}");
        env.Output.WriteLine($"data:    {paths.DataDir}");
        if (paths.ExplicitDbPath)
            env.Output.WriteLine($"db:      {paths.DbPath}");
        return 0;
    }

    private static async Task<int> DoctorAsync(HostCliEnvironment env, string[] args, CancellationToken ct)
    {
        const string usage = "Usage: perga doctor [--deep]";
        if (args.Any(a => a is "-h" or "--help"))
        {
            env.Output.WriteLine(usage);
            return 0;
        }

        var deep = false;
        foreach (var arg in args)
        {
            if (arg == "--deep" && !deep)
                deep = true;
            else
                throw new UsageException($"unexpected argument '{arg}'.", usage);
        }

        var paths = InstallationPaths.From(env);
        var healthy = true;
        void Report(string status, string name, string detail) => env.Output.WriteLine($"[{status}] {name,-10} {detail}");

        var version = ReadVersion(paths.InstallDir);
        Report(version is null ? "warn" : "ok", "version", version ?? "VERSION file is missing");
        Report(Directory.Exists(paths.InstallDir) ? "ok" : "fail", "install", paths.InstallDir);
        if ((env.Get("PERGA_MANAGED_BIN_DIR") ?? env.Get("PERGA_BIN_DIR")) is { } binDir)
        {
            var launcher = Path.Combine(Path.GetFullPath(binDir), "perga");
            Report(IsExecutable(launcher) ? "ok" : "warn", "launcher", IsExecutable(launcher) ? launcher : $"{launcher} is missing or not executable");
        }

        Report(Directory.Exists(paths.DataDir) ? "ok" : "warn", "data",
            Directory.Exists(paths.DataDir) ? paths.DataDir : $"{paths.DataDir} (created on first start)");
        Report(File.Exists(paths.DbPath) ? "ok" : "warn", "database", File.Exists(paths.DbPath)
            ? $"{paths.DbPath} ({new FileInfo(paths.DbPath).Length} bytes)"
            : $"{paths.DbPath} not created yet; run 'perga auth init-owner --username owner --allow-migrate'");

        var missing = RequiredExecutables.Where(f => !IsExecutable(Path.Combine(paths.InstallDir, f)))
            .Concat(RequiredFiles.Where(f => !File.Exists(Path.Combine(paths.InstallDir, f))))
            .ToArray();
        healthy &= missing.Length == 0;
        Report(missing.Length == 0 ? "ok" : "fail", "package",
            missing.Length == 0 ? "all required files present" : $"missing or not executable: {string.Join(", ", missing)}");

        var node = env.ProbeNode(env.Get("PERGA_OAR_NODE_BIN"), env.Variables);
        healthy &= node.MeetsMinimum;
        Report(node.MeetsMinimum ? "ok" : "fail", "node",
            node.MeetsMinimum ? $"v{node.Version} ({node.ExecutablePath})" : node.Error ?? $"Node.js {OarNodeRuntimeProbe.MinimumMajorVersion}+ is required");

        var providers = env.DiscoverProviders(env.Variables, env.HomeDirectory);
        Report(providers.Count > 0 ? "ok" : "warn", "providers", providers.Count > 0
            ? string.Join(", ", providers.Select(p => $"{p.Name} ({p.ExecutablePath})"))
            : $"no provider CLI found; install one of {string.Join(", ", ProviderCliProbe.KnownCommands)}");

        if (deep)
        {
            var bridgePath = Path.Combine(paths.InstallDir, "oar-bridge", "perga-oar-bridge.mjs");
            if (!node.MeetsMinimum || string.IsNullOrWhiteSpace(node.ExecutablePath) || !File.Exists(bridgePath))
            {
                env.Output.WriteLine("[warn] deep-probe skipped (fix package/node checks above first)");
            }
            else
            {
                var statuses = await env.ProbeProvidersDeepAsync(paths.InstallDir, node.ExecutablePath, env.Variables, ct).ConfigureAwait(false);
                var availableCount = 0;
                foreach (var status in statuses)
                {
                    var ready = status.Available && string.IsNullOrWhiteSpace(status.Reason);
                    var ver = string.IsNullOrWhiteSpace(status.Version) ? (ready ? "ready" : "unknown version") : status.Version;
                    if (ready)
                    {
                        availableCount++;
                        var modelCount = status.ModelCatalog?.Count ?? status.Models.Count;
                        var modelWord = modelCount == 1 ? "model" : "models";
                        env.Output.WriteLine($"[ok]   provider:{status.Provider}  {ver} ({modelCount} {modelWord})");
                    }
                    else
                    {
                        var reason = string.IsNullOrWhiteSpace(status.Reason) ? "not ready" : status.Reason;
                        env.Output.WriteLine($"[warn] provider:{status.Provider}  {(status.Available ? ver : "unavailable")} — {reason}");
                        if (DeepProbeFixHint(status.Provider) is { } fix)
                            env.Output.WriteLine($"       fix: {fix}");
                    }
                }

                if (availableCount == 0)
                    env.Output.WriteLine("No provider is ready yet. Install and sign in to at least one CLI above, then re-run 'perga doctor --deep'.");
            }
        }

        env.Output.WriteLine(healthy ? "All required checks passed." : "Required checks failed.");
        return healthy ? 0 : 1;
    }

    private static string? DeepProbeFixHint(string provider) => provider.ToLowerInvariant() switch
    {
        "claude" => "Install Claude Code and run 'claude auth login'",
        "codex" => "npm install -g @openai/codex && codex login",
        "acp:antigravity" or "antigravity" or "agy" =>
            "Install standalone agy >= 1.5.0: curl -fsSL https://antigravity.google/cli/install.sh | bash (not just the IDE app), then sign in with 'agy'",
        "acp:opencode" or "opencode" => "Install OpenCode and sign in with 'opencode'",
        _ => null
    };

    private static async Task<int> UpgradeAsync(HostCliEnvironment env, string[] args, CancellationToken ct)
    {
        const string usage = "Usage: perga upgrade [latest|vX.Y.Z]";
        if (args.Any(a => a is "-h" or "--help"))
        {
            await env.Output.WriteLineAsync(usage).ConfigureAwait(false);
            return 0;
        }

        if (args.FirstOrDefault(a => a.StartsWith('-')) is { } flag)
            throw new UsageException($"unknown option '{flag}'.", usage);
        if (args.Length > 1)
            throw new UsageException("expected at most one version.", usage);

        var version = args.FirstOrDefault() ?? env.Get("PERGA_VERSION") ?? "latest";
        if (version != "latest" && !ReleaseTagPattern().IsMatch(version))
            throw new CliException($"invalid version '{version}'; use 'latest' or a SemVer tag such as v0.1.0.");
        var repo = env.Get("PERGA_REPO") ?? DefaultRepo;
        if (!RepoPattern().IsMatch(repo))
            throw new CliException($"invalid PERGA_REPO '{repo}'; expected owner/repo.");
        var url = env.Get("PERGA_INSTALLER_URL")
                  ?? (repo == DefaultRepo ? DefaultInstallerUrl : $"https://raw.githubusercontent.com/{repo}/main/install.sh");
        if (!url.StartsWith("https://", StringComparison.Ordinal) && !url.StartsWith("file://", StringComparison.Ordinal))
            throw new CliException($"refusing installer URL '{url}'; use https:// or file://.");

        // The installer inherits the launcher's resolved PERGA_INSTALL_DIR, PERGA_BIN_DIR, PERGA_DATA_DIR and PERGA_DB_PATH.
        var childEnv = new Dictionary<string, string>(env.Variables, StringComparer.Ordinal)
        {
            ["PERGA_REPO"] = repo,
            ["PERGA_VERSION"] = version
        };
        var temp = Directory.CreateTempSubdirectory("perga-upgrade-");
        try
        {
            var script = Path.Combine(temp.FullName, "install.sh");
            await env.Output.WriteLineAsync($"Downloading installer from {url}...").ConfigureAwait(false);
            var download = await env.RunProcessAsync("curl",
                ["--fail", "--silent", "--show-error", "--location", "--retry", "3", "--max-time", "60", "--output", script, url],
                childEnv, ct).ConfigureAwait(false);
            if (download != 0 || !File.Exists(script) || new FileInfo(script).Length == 0)
                throw new CliException($"could not download the installer from {url} (curl exit {download}).");
            return await env.RunProcessAsync("bash", [script], childEnv, ct).ConfigureAwait(false);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static int Uninstall(HostCliEnvironment env, string[] args)
    {
        const string usage = "Usage: perga uninstall [--yes]";
        if (args.Any(a => a is "-h" or "--help"))
        {
            env.Output.WriteLine(usage);
            return 0;
        }

        if (args.FirstOrDefault(a => a is not ("-y" or "--yes")) is { } unknown)
            throw new UsageException($"unknown argument '{unknown}'.", usage);
        var plan = UninstallPlan.Resolve(env);

        env.Output.WriteLine("This removes:");
        env.Output.WriteLine($"  install:  {plan.InstallDir}");
        if (plan.Launcher is not null)
            env.Output.WriteLine($"  launcher: {plan.Launcher}");
        env.Output.WriteLine("User data is kept:");
        env.Output.WriteLine($"  data:     {plan.DataDir}");
        env.Output.WriteLine($"  db:       {plan.DbPath}");
        if (args.Length == 0)
        {
            if (!env.IsInteractive)
                throw new CliException("refusing to uninstall without confirmation; re-run with --yes.");
            env.Output.Write("Continue? [y/N] ");
            env.Output.Flush();
            if (env.Input.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                env.Output.WriteLine("Uninstall cancelled.");
                return 1;
            }
        }

        if (plan.Launcher is not null)
            File.Delete(plan.Launcher);
        Directory.Delete(plan.InstallDir, recursive: true);
        env.Output.WriteLine($"Removed Perga from {plan.InstallDir}. Your data remains in {plan.DataDir}.");
        return 0;
    }

    private static bool NoArguments(HostCliEnvironment env, string[] args, string usage)
    {
        if (args is ["-h" or "--help"])
        {
            env.Output.WriteLine(usage);
            return true;
        }

        if (args.Length > 0)
            throw new UsageException($"unexpected argument '{args[0]}'.", usage);
        return false;
    }

    private static string? ReadVersion(string installDir)
    {
        var path = Path.Combine(installDir, "VERSION");
        if (!File.Exists(path)) return null;
        using var reader = new StreamReader(path);
        return reader.ReadLine()?.Trim() is { Length: > 0 } line ? line : null;
    }

    private static bool IsExecutable(string path) =>
        File.Exists(path) && (OperatingSystem.IsWindows() ||
                              (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);

    [GeneratedRegex(@"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?$")]
    internal static partial Regex ReleaseTagPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex RepoPattern();

    private sealed record InstallationPaths(string InstallDir, string DataDir, string DbPath, bool ExplicitDbPath)
    {
        public static InstallationPaths From(HostCliEnvironment env)
        {
            var install = FullPath(env.Get("PERGA_INSTALL_DIR") ?? env.BaseDirectory);
            var data = FullPath(env.Get("PERGA_DATA_DIR") ?? Path.Combine(env.HomeDirectory, ".local", "share", "perga"));
            var db = env.Get("PERGA_DB_PATH");
            return new(install, data, db is null ? Path.Combine(data, "perga.db") : FullPath(db), db is not null);
        }
    }

    /// <summary>The verified targets of <c>perga uninstall</c>. Every check fails closed.</summary>
    private sealed record UninstallPlan(string InstallDir, string? Launcher, string DataDir, string DbPath)
    {
        private static readonly string[] CheckedVariables =
        [
            "PERGA_INSTALL_DIR", "PERGA_MANAGED_INSTALL_DIR", "PERGA_BIN_DIR", "PERGA_MANAGED_BIN_DIR",
            "PERGA_DATA_DIR", "PERGA_DB_PATH"
        ];

        private static readonly string[] AspNetDataDirVariables = ["Perga__DataDir", "Perga__DataDirectory"];
        private const string AspNetDbPathVariable = "Perga__DbPath";

        public static UninstallPlan Resolve(HostCliEnvironment env)
        {
            var exclusionComparison = env.CaseInsensitivePaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var name in CheckedVariables)
            {
                if (env.Get(name) is { } value && value.Any(char.IsControl))
                    throw new CliException($"{name} contains control characters.");
            }

            var aspNetStorage = new List<(string Name, string Value, bool IsDataDir)>();
            foreach (var (key, value) in env.Variables)
            {
                var isDataDir = AspNetDataDirVariables.Any(n => string.Equals(key, n, StringComparison.OrdinalIgnoreCase));
                var isDbPath = !isDataDir && string.Equals(key, AspNetDbPathVariable, StringComparison.OrdinalIgnoreCase);
                if (!isDataDir && !isDbPath) continue;
                if (value.Any(char.IsControl))
                    throw new CliException($"{key} contains control characters.");
                if (value.Length > 0)
                {
                    if (!Path.IsPathRooted(value))
                        throw new CliException($"{key} '{value}' must be an absolute path; refusing to uninstall.");
                    aspNetStorage.Add((key, value, isDataDir));
                }
            }

            var rawBase = FullPath(env.BaseDirectory);
            var rawManaged = env.Get("PERGA_MANAGED_INSTALL_DIR") is { } managedVal ? FullPath(managedVal) : null;
            var rawRequested = env.Get("PERGA_INSTALL_DIR") is { } requestedVal ? FullPath(requestedVal) : null;

            var candidateInstall = rawManaged ?? rawRequested ?? rawBase;
            if (PathsEqual(candidateInstall, Path.GetPathRoot(candidateInstall)!, exclusionComparison) ||
                PathsEqual(rawBase, Path.GetPathRoot(rawBase)!, exclusionComparison))
                throw new CliException($"refusing to uninstall the filesystem root '{candidateInstall}'.");
            if (IsLink(rawBase))
                throw new CliException($"install directory '{rawBase}' is a symlink; refusing to uninstall.");
            if (rawManaged is not null && IsLink(rawManaged))
                throw new CliException($"install directory '{rawManaged}' is a symlink; refusing to uninstall.");
            if (rawRequested is not null && IsLink(rawRequested))
                throw new CliException($"install directory '{rawRequested}' is a symlink; refusing to uninstall.");

            var baseInstall = PhysicalPath(rawBase);
            if (PathsEqual(baseInstall, Path.GetPathRoot(baseInstall)!, exclusionComparison))
                throw new CliException($"refusing to uninstall the filesystem root '{baseInstall}'.");

            var managedPhysical = rawManaged is not null ? PhysicalPath(rawManaged) : null;
            if (managedPhysical is not null && !PathsEqual(managedPhysical, baseInstall, StringComparison.Ordinal))
                throw new CliException($"PERGA_MANAGED_INSTALL_DIR '{rawManaged}' does not match this running installation '{baseInstall}'; refusing to uninstall.");
            if (rawRequested is not null)
            {
                var requestedPhysical = PhysicalPath(rawRequested);
                if (managedPhysical is not null && !PathsEqual(requestedPhysical, managedPhysical, StringComparison.Ordinal))
                    throw new CliException($"PERGA_INSTALL_DIR '{rawRequested}' does not match this launcher's '{rawManaged}'; run the launcher of that instance instead.");
                if (!PathsEqual(requestedPhysical, baseInstall, StringComparison.Ordinal))
                    throw new CliException($"PERGA_INSTALL_DIR '{rawRequested}' does not match this running installation '{baseInstall}'; run that installation instead.");
            }

            var binDir = Managed(env, "PERGA_BIN_DIR", "PERGA_MANAGED_BIN_DIR");
            if (!Directory.Exists(baseInstall))
                throw new CliException($"install directory '{baseInstall}' does not exist.");
            var realInstall = baseInstall;
            var marker = Path.Combine(realInstall, InstallMarker);
            if (!File.Exists(marker) || IsLink(marker))
                throw new CliException($"'{realInstall}' has no regular {InstallMarker} file; refusing to remove a directory the installer did not create.");

            var paths = InstallationPaths.From(env);
            var protectedPaths = new List<(string Name, string Path)>
            {
                ("PERGA_DATA_DIR", paths.DataDir),
                ("database path", paths.DbPath)
            };
            foreach (var (name, value, isDataDir) in aspNetStorage)
            {
                var full = FullPath(value);
                protectedPaths.Add((name, full));
                if (isDataDir)
                    protectedPaths.Add(($"{name} database path", Path.Combine(full, "perga.db")));
            }

            foreach (var (name, path) in protectedPaths)
            {
                if (IsLink(path))
                    throw new CliException($"{name} '{path}' is a symlink; refusing to uninstall.");
                var real = PhysicalPath(path);
                if (IsInside(real, realInstall, exclusionComparison))
                    throw new CliException($"{name} '{real}' is inside the install directory '{realInstall}'; refusing to delete user data.");
            }

            return new(realInstall, binDir is null ? null : VerifiedLauncher(Path.Combine(binDir, "perga"), realInstall), paths.DataDir, paths.DbPath);
        }

        // The launcher pins the instance it belongs to; an override naming another instance is refused.
        private static string? Managed(HostCliEnvironment env, string overrideName, string managedName)
        {
            var requested = env.Get(overrideName) is { } value ? FullPath(value) : null;
            if (env.Get(managedName) is not { } managedValue) return requested;
            var managed = FullPath(managedValue);
            if (requested is not null && !PathsEqual(PhysicalPath(requested), PhysicalPath(managed), StringComparison.Ordinal))
                throw new CliException($"{overrideName} '{requested}' does not match this launcher's '{managed}'; run the launcher of that instance instead.");
            return managed;
        }

        private static string? VerifiedLauncher(string launcher, string realInstall)
        {
            if (!IsLink(launcher) && !File.Exists(launcher) && !Directory.Exists(launcher)) return null;
            if (IsLink(launcher) || !File.Exists(launcher))
                throw new CliException($"launcher '{launcher}' is not a regular file; refusing to uninstall.");
            const string prefix = "DEFAULT_INSTALL_DIR=";
            var expected = $"'{realInstall.Replace("'", "'\\''", StringComparison.Ordinal)}'";
            if (!File.ReadLines(launcher).Any(line =>
                    line.StartsWith(prefix, StringComparison.Ordinal) &&
                    string.Equals(line[prefix.Length..], expected, StringComparison.Ordinal)))
                throw new CliException($"launcher '{launcher}' does not belong to '{realInstall}'; refusing to uninstall.");
            return launcher;
        }

        private static bool PathsEqual(string left, string right, StringComparison comparison) =>
            string.Equals(left, right, comparison);

        private static bool IsInside(string path, string directory, StringComparison comparison) =>
            PathsEqual(path, directory, comparison) ||
            path.StartsWith(directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar, comparison);
    }

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) is { Length: > 0 } full ? full : "/";

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>Resolves symlinks in every existing component; a missing tail is appended as written.</summary>
    public static string PhysicalPath(string path, int depth = 0)
    {
        if (depth > 32) throw new CliException($"too many levels of symbolic links in '{path}'.");
        var full = FullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new FileInfo(current) is { LinkTarget: not null } link)
            {
                var target = link.ResolveLinkTarget(returnFinalTarget: true)
                             ?? throw new CliException($"cannot resolve symlink '{current}'.");
                current = PhysicalPath(target.FullName, depth + 1);
            }
        }

        return current;
    }

    private class CliException(string message) : Exception(message);

    private sealed class UsageException(string message, string usage) : CliException(message)
    {
        public string Usage { get; } = usage;
    }
}
