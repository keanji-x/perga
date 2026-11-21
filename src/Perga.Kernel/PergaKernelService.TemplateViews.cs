using System.Text.Json;
using Perga.PolicyVm;
using Perga.Contracts;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public Task<TemplateViewsProjection> GetTrackViewsAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).GetTemplateViewsAsync(ct);

    public Task<TrackDataPatchResult> PatchTrackDataAsync(
        string trackId,
        JsonElement patch,
        string source,
        string? callerSessionId = null,
        CancellationToken ct = default,
        long? expectedDataRev = null) =>
        Actors.GetOrActivate(trackId).PatchTemplateDataAsync(patch, source, callerSessionId, ct, expectedDataRev);
}
