using System.Text.Json;
using Perga.PolicyVm;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public Task<TemplateViewsProjection> GetTrackViewsAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).GetTemplateViewsAsync(ct);

    public Task<JsonElement> PatchTrackDataAsync(
        string trackId,
        JsonElement patch,
        string source,
        string? callerSessionId = null,
        CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).PatchTemplateDataAsync(patch, source, callerSessionId, ct);
}
