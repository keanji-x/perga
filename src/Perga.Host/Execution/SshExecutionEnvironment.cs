using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Host.Execution;

public sealed class SshExecutionEnvironment : IExecutionEnvironment
{
    private readonly SshTargetConfig _config;
    private readonly SshTerminalBridgeClient _terminals;
    private int _disposed;

    public SshExecutionEnvironment(SshTargetConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        Descriptor = new ExecutionTargetDto(
            Id: config.TargetId,
            Label: config.Label,
            Kind: "ssh",
            IsDefault: false);

        var transport = new SshSubprocessTransport(config);
        AgentRuntime = new AgentRuntime(new AgentRuntimeOptions
        {
            NodeExecutablePath = config.OarNodeExecutablePath,
            BridgeScriptPath = config.OarBridgeScriptPath ?? config.HostOarBridgeScriptPath,
            SubprocessTransport = transport,
            ProviderOverrides = config.ProviderExecutables.ToDictionary(
                entry => Perga.AgentKit.AgentRuntime.NormalizeProviderName(entry.Key),
                entry => new ProviderCommandOverride(entry.Value),
                StringComparer.OrdinalIgnoreCase)
        });

        var fsOps = new SshWorkspaceFileSystemOps(config);
        var processRunner = new SshProcessRunner(config);
        Vcs = new VcsService(processRunner, fsOps: fsOps);
        Forge = new GitHubForgeService(processRunner, fsOps: fsOps);
        // OverlayFS is intentionally excluded on SSH targets (direct + git/jj/copy isolation only).
        WorkspaceDrivers = new WorkspaceDriverRegistry(
        [
            new DirectWorkspaceDriver(fsOps),
            new VcsWorkspaceDriver(Vcs, fsOps)
        ]);

        Shell = new SshShellCommandRunner(config);
        Files = new SshExecutionFileSystem(config);
        _terminals = new SshTerminalBridgeClient(config);
    }

    public ExecutionTargetDto Descriptor { get; }
    public string WorkspacesRoot => _config.WorkspacesRoot;
    public bool IsLocal => false;
    public IAgentRuntime AgentRuntime { get; }
    public IShellCommandRunner Shell { get; }
    public IVcsService Vcs { get; }
    public IGitHubForgeService? Forge { get; }
    public IWorkspaceDriverRegistry WorkspaceDrivers { get; }
    public IExecutionFileSystem Files { get; }
    public ITerminalBridgeClient Terminals => _terminals;

    public Task<string> ResolveMcpBaseUriAsync(string hostMcpBaseUri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(_config.McpBaseUriOverride))
        {
            return Task.FromResult(_config.McpBaseUriOverride);
        }

        if (_config.IsLoopbackHost)
        {
            return Task.FromResult(hostMcpBaseUri);
        }

        throw KernelException.PreconditionFailed(
            $"Remote SSH target '{_config.TargetId}' ({_config.Host}) requires ExecutionTargets:{_config.TargetId}:McpBaseUriOverride " +
            $"pointing to a reachable Host MCP endpoint.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _terminals.DisposeAsync().ConfigureAwait(false);
        await SshCommandBuilder.TryCloseControlMasterAsync(_config).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_config.ControlDir))
            {
                Directory.Delete(_config.ControlDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp socket directory cleanup.
        }
    }
}
