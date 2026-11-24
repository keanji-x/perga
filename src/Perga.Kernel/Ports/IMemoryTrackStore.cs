namespace Perga.Kernel.Ports;

public sealed record MemoryTrackBinding(string ScopeKey, string? AreaId, string TrackId)
{
    public const string SystemAreaId = "sys_memory";
    public const string McpServerName = "perga_memory";

    public static string ScopeKeyFor(string? areaId) => areaId is null ? "global" : $"area:{areaId}";
}

public interface IMemoryTrackStore
{
    Task<string?> GetMemoryTrackIdAsync(string scopeKey, CancellationToken ct);
    Task<MemoryTrackBinding?> FindMemoryBindingByTrackIdAsync(string trackId, CancellationToken ct);
    Task<bool> IsUserAreaAsync(string areaId, CancellationToken ct);
    Task<string?> GetLatestMemoryContextModeAsync(string sessionId, int epoch, CancellationToken ct);

    /// <summary>Delete only after the observed bound Track, if any, has been torn down.</summary>
    Task DeleteAreaWithMemoryTrackAsync(string areaId, string? expectedMemoryTrackId, CancellationToken ct);
}
