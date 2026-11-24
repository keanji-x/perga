using Perga.Host.Auth;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.MemoryKit;

namespace Perga.Host.Http;

public sealed record MemoryMentionDto(string Ref, string Scope, string? Key, string Title, string? Summary,
    bool Pinned, string Insert);

public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var memory = endpoints.MapGroup("/api/v1/memory").RequireAuthorization(PergaAuthPolicies.UserSession);

        memory.MapPost("/global/track", async (IPergaKernel kernel, CancellationToken ct) =>
            TypedResults.Json(await kernel.GetOrEnsureMemoryTrackAsync(null, ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions))
            .WithMetadata(new RequestOriginGuard.BrowserWriteApi(), new NoRequestBody());

        memory.MapPost("/areas/{areaId}/track", async (string areaId, IPergaKernel kernel, CancellationToken ct) =>
            TypedResults.Json(await kernel.GetOrEnsureMemoryTrackAsync(areaId, ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions))
            .WithMetadata(new RequestOriginGuard.BrowserWriteApi(), new NoRequestBody());

        memory.MapGet("/mentions", async (string? areaId, string? q, IMemoryTrackStore tracks, IMemoryEngine engine,
            CancellationToken ct) =>
        {
            if (areaId is not null) await RequireAreaAsync(tracks, areaId, ct).ConfigureAwait(false);
            var entries = await engine.ListAsync(new(areaId), ct).ConfigureAwait(false);
            var matches = Mentions(entries, q);
            return TypedResults.Json(matches, PergaJsonDefaults.ApiOptions);
        });
    }

    private static async Task RequireAreaAsync(IMemoryTrackStore tracks, string areaId, CancellationToken ct)
    {
        if (!await tracks.IsUserAreaAsync(areaId, ct).ConfigureAwait(false))
            throw KernelException.NotFound($"Area '{areaId}' not found.");
    }

    private static MemoryMentionDto[] Mentions(IReadOnlyList<MemoryEntryMeta> entries, string? query)
    {
        const int maxMentions = 24;
        var q = query?.Trim() ?? "";
        if (q.StartsWith("memory:", StringComparison.OrdinalIgnoreCase) ||
            q.StartsWith("memory：", StringComparison.OrdinalIgnoreCase))
            q = q["memory:".Length..].TrimStart();

        var result = new List<MemoryMentionDto>(maxMentions);
        AddScope("area", "Current Area memory", MemoryScopeKind.Area);
        if (result.Count < maxMentions) AddScope("global", "Global memory", MemoryScopeKind.Global);
        return result.ToArray();

        bool Matches(string value) => value.Contains(q, StringComparison.OrdinalIgnoreCase);

        void AddScope(string scope, string title, MemoryScopeKind kind)
        {
            var scopedEntries = entries.Where(entry => entry.Ref.Kind == kind);
            if ((Matches(scope) || Matches(title)) && scopedEntries.Any())
                result.Add(new(scope, scope, null, title, null, false, Insert(scope)));

            foreach (var entry in scopedEntries)
            {
                if (result.Count == maxMentions) break;
                var reference = entry.Ref.ToString();
                if (Matches(reference) || Matches(entry.Title) || Matches(entry.Summary) || entry.Tags.Any(Matches))
                    result.Add(new(reference, scope, entry.Ref.Key, entry.Title, entry.Summary, entry.Pinned, Insert(reference)));
            }
        }
    }

    private static string Insert(string address) => $"@`memory:{address}`";
}
