using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Templates;
using Perga.PolicyVm;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    public Task<TemplateViewsProjection> GetTemplateViewsAsync(CancellationToken ct = default) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var flowProjection = await BuildFlowProjectionCoreAsync(ct).ConfigureAwait(false);
            var flowElement = JsonHelpers.ToElement(flowProjection);
            if (_policy is null)
            {
                return new TemplateViewsProjection(
                    TemplateId: _track.TemplateId ?? string.Empty,
                    Views: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                    Widgets: new Dictionary<string, string>(StringComparer.Ordinal),
                    Diagnostics: [],
                    DataRev: _track.DataRev,
                    FlowRev: _track.FlowRev,
                    Flow: flowElement);
            }

            var kernelSnapshot = KernelFactsSnapshot.BuildKernelFactsSnapshot(TrackId, _taskCoordinator.Snapshot(), _sessions, flowProjection);
            var track = await ProjectMemoryDataCoreAsync(ct).ConfigureAwait(false);
            return _policyVm.ProjectViews(
                _policy,
                track.TemplateInputs,
                track.Data,
                kernelSnapshot) with
            {
                DataRev = _track.DataRev,
                FlowRev = _track.FlowRev,
                Flow = flowElement
            };
        }, ct);

    public Task<TrackDataPatchResult> PatchTemplateDataAsync(
        JsonElement patch,
        string source,
        string? callerSessionId = null,
        CancellationToken ct = default,
        long? expectedDataRev = null,
        string? expectedPolicyGeneration = null, InteropPrincipal.Session? authorization = null) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireMcpAuthorizationCore(authorization);
            RequireMutableTemplateCore();
            if (expectedPolicyGeneration is not null && _policyGeneration != expectedPolicyGeneration)
                throw KernelException.PolicyDenied("Active policy changed after data patch authorization.");
            if (_track.ClosedAtMs is not null)
            {
                throw KernelException.Conflict("Cannot patch template data on a closed Track.");
            }
            if (_policy is null)
            {
                throw KernelException.Conflict("Track does not have an active template; cannot patch template data.");
            }

            if (!string.IsNullOrWhiteSpace(callerSessionId))
            {
                var caller = RequireSessionCore(callerSessionId);
                if (_taskCoordinator.TryGetActiveOwningTaskCore(caller.Id) is { Access: AccessMode.ReadOnly })
                    throw KernelException.PolicyDenied(
                        "Read-only bound worker sessions cannot patch track template data.");
            }

            if (expectedDataRev is { } expected && expected != _track.DataRev)
                throw KernelException.Conflict($"Data revision conflict: expected {expected}, current {_track.DataRev}.");

            var knownTaskKeys = _taskCoordinator.Snapshot().Select(t => t.TaskKey).Distinct(StringComparer.Ordinal).ToArray();
            var nowIso = _timeProvider.GetUtcNow().ToString("O");
            DataPatchValidationResult result;
            try
            {
                result = _policyVm.ValidateAndPatchData(
                    _policy,
                    _track.TemplateInputs,
                    _track.Data,
                    patch,
                    knownTaskKeys,
                    nowIso,
                    string.IsNullOrWhiteSpace(source) ? "system" : source);
            }
            catch (InvalidOperationException ex)
            {
                throw KernelException.InvalidArgument($"Template data could not be validated: {ex.Message}");
            }

            if (!result.Valid)
            {
                var detail = string.Join("; ", result.Errors.Select(e =>
                    $"{(string.IsNullOrEmpty(e.Path) ? "$" : e.Path)}: {e.Message}"));
                throw KernelException.InvalidArgument($"Template data failed schema validation: {detail}");
            }

            if (result.NextData.ValueKind != JsonValueKind.Undefined &&
                !JsonElement.DeepEquals(_track.Data, result.NextData))
            {
                var now = NowMs();
                var nextTrack = _track with { Data = result.NextData.Clone(), DataRev = checked(_track.DataRev + 1), UpdatedAtMs = now };
                var evt = new EventEnvelopeDto(
                    EventId: null,
                    AreaId: nextTrack.AreaId,
                    TrackId: TrackId,
                    Kind: KernelEventKinds.TrackUpdated,
                    Payload: JsonHelpers.ToElement(TemplatePresentation.Project(nextTrack, _policy, _policyVm)),
                    CreatedAtMs: now);
                await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
                await _store.UpdateTrackAndEmitAsync(nextTrack, evt, ct, writeData: true,
                    beforeWrite: CaptureMcpWriteFenceCore(authorization)).ConfigureAwait(false);
                _track = nextTrack;
                // Existing approvals evaluated the previous policy data snapshot.
                _policyGeneration = Guid.NewGuid().ToString("N");
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
            }

            return new TrackDataPatchResult(true, _track.DataRev, _track.Data);
        }, ct);
}
