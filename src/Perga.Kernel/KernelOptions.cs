using Perga.AgentKit;

namespace Perga.Kernel;

public sealed record KernelOptions
{
    public SystemUpgrade.DispatchAdmission DispatchAdmission { get; init; } = new();
    public string Version { get; init; } = "1.0.0";
    public string? TemplatesDirectory { get; init; }
    public string WorkspacesRootDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "perga-workspaces");
    public string DefaultProvider { get; init; } = "codex";
    public string? DefaultModel { get; init; }
    public bool WorkerApprovalAuthorizationConfigured { get; init; }
    public IReadOnlyDictionary<string, string[]> DispatcherWorkerApprovalOptions { get; init; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal);
    public string DefaultWorkspaceDriver { get; init; } = "direct";
    public TimeSpan ActorIdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ActorEvictionSweepInterval { get; init; } = TimeSpan.FromMinutes(1);
    public double AutoCompactionThreshold { get; init; } = 0.80;
    public OsSandboxMode LeadSandboxMode { get; init; } = OsSandboxMode.None;
    public OsSandboxMode FreeSubagentSandboxMode { get; init; } = OsSandboxMode.None;
    public OsSandboxMode ReadOnlyTaskSandboxMode { get; init; } = OsSandboxMode.ReadOnlyBwrap;
    public OsSandboxMode ReadWriteTaskSandboxMode { get; init; } = OsSandboxMode.WorkspaceWriteBwrap;
    public string McpBaseUri { get; init; } = "http://127.0.0.1:5000";
    public bool DispatchWakesInBackground { get; init; } = false;
    public int DefaultHookTimeoutSecs { get; init; } = 300;
    public static readonly TimeSpan DefaultProviderCancelDispatchGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultProviderTerminalStopGrace = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SessionResumeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public TimeSpan ProviderCancelDispatchGrace
    {
        get;
        init => field = NormalizeStopGrace(value, MaxTimerDelay - SessionResumeTimeout, DefaultProviderCancelDispatchGrace);
    } = DefaultProviderCancelDispatchGrace;

    public TimeSpan ProviderTerminalStopGrace
    {
        get;
        init => field = NormalizeStopGrace(value, MaxTimerDelay, DefaultProviderTerminalStopGrace);
    } = DefaultProviderTerminalStopGrace;

    private static TimeSpan NormalizeStopGrace(TimeSpan value, TimeSpan max, TimeSpan fallback) =>
        value >= TimeSpan.FromMilliseconds(1) && value <= max ? value : fallback;
}
