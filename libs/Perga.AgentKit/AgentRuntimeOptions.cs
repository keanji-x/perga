using System.Runtime.InteropServices;

namespace Perga.AgentKit;

public sealed record ProviderCommandOverride(
    string FileName,
    IReadOnlyList<string>? PrependArgs = null,
    IReadOnlyDictionary<string, string>? Env = null,
    IReadOnlyList<string>? Models = null);

public sealed record OsSandboxOptions
{
    public string BwrapExecutablePath { get; init; } = "/usr/bin/bwrap";
    public string MacSandboxExecPath { get; init; } = "/usr/bin/sandbox-exec";
    public OSPlatform? PlatformOverride { get; init; }
    public Version? BwrapVersionOverride { get; init; }
    public string? HomeDirectoryOverride { get; init; }
    public IReadOnlyList<string> ExtraReadOnlyPaths { get; init; } = [];
    public bool RequireNativeOverlaySupport { get; init; }
}

public sealed record AgentRuntimeOptions
{
    public IReadOnlyDictionary<string, ProviderCommandOverride> ProviderOverrides { get; init; } =
        new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase);

    public OsSandboxOptions Sandbox { get; init; } = new();

    public string? SandboxStateRootDirectory { get; init; }

    public string? CodexSandboxApiKey { get; init; }

    public string? CodexWorkerHomeDirectory { get; init; }

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ModelProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan ProbeCacheTtl { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
