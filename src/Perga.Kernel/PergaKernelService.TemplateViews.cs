using System.Text.Json;
using Perga.PolicyVm;
using Perga.Contracts;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public Task<TemplateViewsProjection> GetTrackViewsAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).GetTemplateViewsAsync(ct);

    public Task<FlowSnapshotDto> GetTrackFlowAsync(string trackId, CancellationToken ct = default) =>
        Actors.GetOrActivate(trackId).GetFlowSnapshotAsync(ct);

    public Task<FlowCommitResultDto> CommitTrackFlowAsync(
        string trackId,
        bool dryRun,
        long? expectedFlowRev,
        InteropPrincipal caller,
        CancellationToken ct = default,
        string? content = null) =>
        Actors.GetOrActivate(trackId).CommitFlowAsync(dryRun, expectedFlowRev, caller, ct, content);

    public Task<TrackDataPatchResult> PatchTrackDataAsync(
        string trackId,
        JsonElement patch,
        string source,
        string? callerSessionId = null,
        CancellationToken ct = default,
        long? expectedDataRev = null) =>
        Actors.GetOrActivate(trackId).PatchTemplateDataAsync(patch, source, callerSessionId, ct, expectedDataRev);
}
