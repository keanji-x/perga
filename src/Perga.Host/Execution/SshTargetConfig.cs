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

    public string Destination => $"{User}@{Host}";

    public bool IsLoopbackHost =>
        string.Equals(Host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Host, "::1", StringComparison.Ordinal);

    public SshTargetConfig(
        string targetId,
        ExecutionTargetOptions options,
        string? hostOarBridgeScriptPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(options);

        TargetId = targetId.Trim();
        if (string.Equals(TargetId, "local", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Execution target id 'local' is reserved.", nameof(targetId));
        }

        Host = string.IsNullOrWhiteSpace(options.Host) ? "127.0.0.1" : options.Host.Trim();
        Port = options.Port is > 0 and <= 65535 ? options.Port : 22;
        User = !string.IsNullOrWhiteSpace(options.User)
            ? options.User.Trim()
            : throw new ArgumentException($"Execution target '{TargetId}' must specify a non-empty User.", nameof(options));

        Label = string.IsNullOrWhiteSpace(options.Label)
            ? (IsLoopbackHost ? $"{User} (local ssh)" : $"{User}@{Host}")
            : options.Label.Trim();

        IdentityFile = string.IsNullOrWhiteSpace(options.IdentityFile) ? null : options.IdentityFile.Trim();
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
}
