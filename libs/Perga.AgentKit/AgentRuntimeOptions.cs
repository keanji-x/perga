namespace Perga.AgentKit;

public sealed record ProviderCommandOverride(
    string FileName,
    IReadOnlyDictionary<string, string>? Env = null);

public sealed record AgentRuntimeOptions
{
    public IReadOnlyDictionary<string, ProviderCommandOverride> ProviderOverrides { get; init; } =
        new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase);

    public string? NodeExecutablePath { get; init; }

    public string? BridgeScriptPath { get; init; }

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ProbeCacheTtl { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
