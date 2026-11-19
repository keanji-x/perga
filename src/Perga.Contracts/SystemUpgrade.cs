using System.Text.Json.Serialization;

namespace Perga.Contracts;

public sealed record SystemInfoDto(string ReleaseId, string WebBuildId, string HostBuildId,
    string BridgeBuildId, int SchemaVersion, bool Draining, int ActiveDispatches);
public sealed record SystemDrainRequest([property: JsonRequired] bool Draining);
public sealed record SystemDrainResponse(bool Draining, int ActiveDispatches);
public sealed record SystemReloadRequest([property: JsonRequired] string ReleaseId, [property: JsonRequired] string WebBuildId);
public sealed record SystemReloadResponse(string ReleaseId, string WebBuildId);
public sealed record SystemUpgradeStatusPayload(bool Draining, int ActiveDispatches, SystemUpgradeReceipt? Receipt = null);
public sealed record SystemUpgradeReceipt(string JobId, string? OldReleaseId, string? NewReleaseId,
    string Status, string? Error, string? RollbackError, string? NotifyTrackId, double FinishedAt,
    IReadOnlyList<string> Changed);
public sealed record SystemUpgradeHandoff(string JobId, string Status);
