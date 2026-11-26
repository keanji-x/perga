using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

/// <summary>The Node.js runtime the OAR bridge would use, as found by <see cref="OarNodeRuntimeProbe"/>.</summary>
public sealed record OarNodeRuntimeStatus(bool Found, bool MeetsMinimum, Version? Version, string? ExecutablePath, string? Error);

/// <summary>Diagnoses the OAR bridge Node.js runtime with the same resolution rules the bridge launcher uses.</summary>
public static class OarNodeRuntimeProbe
{
    public const int MinimumMajorVersion = OarNodeResolver.MinimumNodeMajorVersion;

    public static OarNodeRuntimeStatus Probe(string? configuredExecutable, IReadOnlyDictionary<string, string>? environment) =>
        Probe(configuredExecutable, environment, includeSystemLocations: environment is null);

    public static OarNodeRuntimeStatus Probe(
        string? configuredExecutable,
        IReadOnlyDictionary<string, string>? environment,
        bool includeSystemLocations = false) =>
        OarNodeResolver.Probe(configuredExecutable, environment, includeSystemLocations);
}
