using System.Text.Json.Serialization;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public sealed record AreaDiscoveryResult(string CurrentAreaId, IReadOnlyList<AreaDiscoveryEntry> Areas);

public sealed record AreaDiscoveryEntry(
    string Id,
    string Name,
    bool IsCurrent,
    int OpenTrackCount,
    int ClosedTrackCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DefaultCwd);

public sealed record TrackDiscoveryResult(string Scope, TrackDiscoveryArea? Area, IReadOnlyList<TrackDiscoveryEntry> Tracks);

public sealed record TrackDiscoveryArea(string Id, string Name);

public sealed record TrackDiscoveryEntry(
    string Id,
    string Title,
    string AreaId,
    string AreaName,
    string ReportSummary,
    bool IsCurrent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ClosedAtMs,
    long UpdatedAtMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OriginTrackId = null);

public sealed partial class PergaKernelService
{
    public async Task<AreaDiscoveryResult> DiscoverAreasAsync(string currentTrackId, CancellationToken ct = default)
    {
        var currentAreaId = await GetCurrentAreaIdAsync(currentTrackId, ct).ConfigureAwait(false);
        var areas = await _store.ListAreasAsync(ct, includeSystem: true).ConfigureAwait(false);
        var counts = await _store.GetAreaTrackCountsAsync(ct).ConfigureAwait(false);
        var entries = areas.Where(area => area.Id != MemoryTrackBinding.SystemAreaId).Select(area =>
        {
            var count = counts.GetValueOrDefault(area.Id);
            return new AreaDiscoveryEntry(area.Id, area.Name, area.Id == currentAreaId,
                count?.OpenCount ?? 0, count?.ClosedCount ?? 0, area.DefaultCwd);
        }).ToArray();
        return new AreaDiscoveryResult(currentAreaId, entries);
    }

    public async Task<TrackDiscoveryResult> DiscoverTracksAsync(
        string currentTrackId, string? area, string? query, bool includeClosed, CancellationToken ct = default)
    {
        var (scope, selectedArea) = await ResolveDiscoveryScopeAsync(currentTrackId, area, ct).ConfigureAwait(false);
        var tracks = await _store.ListTracksIncludingSystemAsync(selectedArea?.Id, includeClosed, ct).ConfigureAwait(false);
        var areas = selectedArea is null ? await _store.ListAreasAsync(ct, includeSystem: true).ConfigureAwait(false) : [selectedArea];
        var areasById = areas.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var summaries = await _store.GetReportSummariesAsync(tracks.Select(track => track.Id).ToArray(), ct).ConfigureAwait(false);
        var entries = new List<TrackDiscoveryEntry>(tracks.Count);
        foreach (var track in tracks)
        {
            if (track.AreaId == MemoryTrackBinding.SystemAreaId || !areasById.TryGetValue(track.AreaId, out var trackArea))
                continue;

            var summary = summaries.GetValueOrDefault(track.Id, string.Empty);
            if (query is not null &&
                !track.Title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !summary.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !track.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(new TrackDiscoveryEntry(track.Id, track.Title, track.AreaId,
                trackArea.Name, summary, track.Id == currentTrackId, track.ClosedAtMs, track.UpdatedAtMs, track.OriginTrackId));
        }

        return new TrackDiscoveryResult(scope,
            selectedArea is null ? null : new TrackDiscoveryArea(selectedArea.Id, selectedArea.Name), entries);
    }

    private async Task<string> GetCurrentAreaIdAsync(string trackId, CancellationToken ct)
    {
        var track = await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Track '{trackId}' not found.");
        if (track.AreaId == MemoryTrackBinding.SystemAreaId &&
            await _store.FindMemoryBindingByTrackIdAsync(trackId, ct).ConfigureAwait(false) is { AreaId: { } areaId })
            return areaId;
        return track.AreaId;
    }

    private async Task<(string Scope, AreaDto? Area)> ResolveDiscoveryScopeAsync(
        string currentTrackId, string? area, CancellationToken ct)
    {
        if (area is null)
        {
            var current = await GetCurrentAreaIdAsync(currentTrackId, ct).ConfigureAwait(false);
            if (current == MemoryTrackBinding.SystemAreaId) return ("all", null);
            return ("current_area", await _store.GetAreaAsync(current, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound($"Area '{current}' not found."));
        }
        if (area == "*")
            return ("all", null);

        return ("area", await ResolveAreaByIdOrNameAsync(area, ct).ConfigureAwait(false));
    }

    private async Task<AreaDto> ResolveAreaByIdOrNameAsync(string area, CancellationToken ct)
    {
        var areas = (await _store.ListAreasAsync(ct, includeSystem: true).ConfigureAwait(false))
            .Where(item => item.Id != MemoryTrackBinding.SystemAreaId).ToArray();
        var selectedArea = areas.FirstOrDefault(item => item.Id == area);
        if (selectedArea is not null)
            return selectedArea;

        var name = area.Trim();
        var matches = areas.Where(item => string.Equals(item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            0 => throw KernelException.NotFound($"Area '{area}' not found. Call area_list to inspect available Areas."),
            1 => matches[0],
            _ => throw KernelException.InvalidArgument(
                $"Area name '{area}' is ambiguous. Specify one of these Area IDs: {string.Join(", ", matches.Select(item => item.Id))}.")
        };
    }
}
