using System.Text.Json;

namespace Perga.Kernel.Ports;

public sealed record MemoryTurnSnapshot(string Content, string Version, int EntryCount);

public interface IMemoryContextProvider
{
    Task<JsonElement?> GetMemoryTemplateDataAsync(string? areaId, CancellationToken ct) =>
        Task.FromResult<JsonElement?>(null);
    Task<MemoryTurnSnapshot?> GetContextSnapshotAsync(string? areaId, CancellationToken ct);
    Task<string> AutoRecallAsync(string? areaId, string ownerContent, CancellationToken ct);
    Task<string> HydrateMentionsAsync(string? areaId, string ownerContent, CancellationToken ct);
    Task DeleteAreaDirectoryAsync(string areaId, CancellationToken ct);
    Task CleanupOrphanAreaDirectoriesAsync(IReadOnlySet<string> validUserAreaIds, CancellationToken ct);
}

internal sealed class NoMemoryContextProvider : IMemoryContextProvider
{
    internal static readonly IMemoryContextProvider Instance = new NoMemoryContextProvider();

    public Task<MemoryTurnSnapshot?> GetContextSnapshotAsync(string? areaId, CancellationToken ct) =>
        Task.FromResult<MemoryTurnSnapshot?>(null);
    public Task<string> AutoRecallAsync(string? areaId, string ownerContent, CancellationToken ct) =>
        Task.FromResult("");
    public Task<string> HydrateMentionsAsync(string? areaId, string ownerContent, CancellationToken ct) =>
        Task.FromResult("");
    public Task DeleteAreaDirectoryAsync(string areaId, CancellationToken ct) => Task.CompletedTask;
    public Task CleanupOrphanAreaDirectoriesAsync(IReadOnlySet<string> validUserAreaIds, CancellationToken ct) => Task.CompletedTask;
}
