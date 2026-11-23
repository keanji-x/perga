using Perga.Contracts;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    public Task<TrackDto> ApplyTemplateByHumanAsync(ApplyTrackTemplateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot set a template on a closed track.");

            var result = await ApplyTemplateSelectionCoreAsync(
                callerSessionId: null,
                templateId: request.TemplateId,
                source: request.Source,
                workspaceSource: null,
                path: null,
                inputs: request.Inputs,
                dryRun: false,
                clear: false,
                resetData: request.ResetData,
                seedReport: request.SeedReport,
                authorization: null,
                ct: ct).ConfigureAwait(false);
            return result.Track;
        }, ct);
    }
}
