using System.Security.Cryptography;
using System.Text;
using Perga.Host.Configuration;

namespace Perga.Host.Execution;

public sealed class SshTargetConfig
{
    public string TargetId { get; }
    public string Label { get; }
    public string Host { get; }
    public int Port { get; }
    public string User { get; }
    public string? IdentityFile { get; }
    public string? KnownHostsFile { get; }
    public string StrictHostKeyChecking { get; }
    public string WorkspacesRoot { get; }
    public string? TerminalBridgeExecutablePath { get; }
    public string? OarNodeExecutablePath { get; }
    public string? OarBridgeScriptPath { get; }
    public string? HostOarBridgeScriptPath { get; }
    public string? McpBaseUriOverride { get; }
    public string SshExecutablePath { get; }
    public IReadOnlyDictionary<string, string> ExtraEnv { get; }
    public IReadOnlyDictionary<string, string> ProviderExecutables { get; }
    public string ControlDir { get; }
    public string ControlPath { get; }
    public bool Removable { get; }

    public string Destination => $"{User}@{Host}";

    public bool IsLoopbackHost => IsLoopback(Host);

    internal static bool IsLoopback(string host) =>
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::1", StringComparison.Ordinal);

    internal static string DefaultLabel(string user, string host, int port) =>
        IsLoopback(host) ? user : (port == 22 ? $"{user}@{host}" : $"{user}@{host}:{port}");

    public SshTargetConfig(
        string targetId,
        ExecutionTargetOptions options,
        string? hostOarBridgeScriptPath = null,
        bool removable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(options);

        TargetId = targetId.Trim();
        if (string.Equals(TargetId, "local", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Execution target id 'local' is reserved.", nameof(targetId));
        }

        Host = string.IsNullOrWhiteSpace(options.Host) ? "127.0.0.1" : options.Host.Trim();
        ValidateSshToken(Host, "Host");
        Port = options.Port is > 0 and <= 65535 ? options.Port : 22;
        User = !string.IsNullOrWhiteSpace(options.User)
            ? options.User.Trim()
            : throw new ArgumentException($"Execution target '{TargetId}' must specify a non-empty User.", nameof(options));
        ValidateSshToken(User, "User");

        Label = string.IsNullOrWhiteSpace(options.Label)
            ? DefaultLabel(User, Host, Port)
            : options.Label.Trim();

        IdentityFile = string.IsNullOrWhiteSpace(options.IdentityFile) ? null : options.IdentityFile.Trim();
        if (IdentityFile is not null)
        {
            ValidateSshPath(IdentityFile, "IdentityFile");
        }
        KnownHostsFile = string.IsNullOrWhiteSpace(options.KnownHostsFile) ? null : options.KnownHostsFile.Trim();
        StrictHostKeyChecking = string.IsNullOrWhiteSpace(options.StrictHostKeyChecking)
            ? "accept-new"
            : options.StrictHostKeyChecking.Trim();

        WorkspacesRoot = !string.IsNullOrWhiteSpace(options.WorkspacesRoot)
            ? options.WorkspacesRoot.Trim().TrimEnd('/')
            : $"/home/{User}/.perga/workspaces";
        if (string.IsNullOrEmpty(WorkspacesRoot))
        {
            WorkspacesRoot = "/";
        }

        Removable = removable;

        TerminalBridgeExecutablePath = string.IsNullOrWhiteSpace(options.TerminalBridgeExecutablePath)
            ? null
            : options.TerminalBridgeExecutablePath.Trim();
        OarNodeExecutablePath = string.IsNullOrWhiteSpace(options.OarNodeExecutablePath)
            ? null
            : options.OarNodeExecutablePath.Trim();
        OarBridgeScriptPath = string.IsNullOrWhiteSpace(options.OarBridgeScriptPath)
            ? null
            : options.OarBridgeScriptPath.Trim();
        HostOarBridgeScriptPath = string.IsNullOrWhiteSpace(hostOarBridgeScriptPath)
            ? null
            : hostOarBridgeScriptPath.Trim();
        McpBaseUriOverride = string.IsNullOrWhiteSpace(options.McpBaseUriOverride)
            ? null
            : options.McpBaseUriOverride.Trim();
        SshExecutablePath = string.IsNullOrWhiteSpace(options.SshExecutablePath)
            ? "ssh"
            : options.SshExecutablePath.Trim();

        ExtraEnv = new Dictionary<string, string>(options.ExtraEnv, StringComparer.Ordinal);
        ProviderExecutables = new Dictionary<string, string>(options.ProviderExecutables, StringComparer.OrdinalIgnoreCase);

        // Keep Unix domain socket paths short (< 104 bytes) and private (0700).
        var hashInput = $"{TargetId}|{Destination}:{Port}|{Environment.ProcessId}|{Guid.NewGuid():N}";
        var shortHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)))[..12].ToLowerInvariant();
        var tmpBase = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        ControlDir = Path.Combine(tmpBase, $"pg-ssh-{shortHash}");
        ControlPath = Path.Combine(ControlDir, "cm.sock");
    }

    public void EnsureControlDirectoryExists()
    {
        if (Directory.Exists(ControlDir))
        {
            return;
        }

        Directory.CreateDirectory(ControlDir);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    ControlDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Best-effort permission tightening.
            }
        }
    }

    internal static void ValidateSshToken(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"SSH {fieldName} cannot be empty.", fieldName);
        }

        if (value[0] == '-')
        {
            throw new ArgumentException($"SSH {fieldName} cannot start with '-'.", fieldName);
        }

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                throw new ArgumentException($"SSH {fieldName} cannot contain whitespace or control characters.", fieldName);
            }
        }
    }

    internal static void ValidateSshPath(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"SSH {fieldName} cannot be empty.", fieldName);
        }

        if (value[0] == '-')
        {
            throw new ArgumentException($"SSH {fieldName} cannot start with '-'.", fieldName);
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                throw new ArgumentException($"SSH {fieldName} cannot contain control characters.", fieldName);
            }
        }
    }
}
