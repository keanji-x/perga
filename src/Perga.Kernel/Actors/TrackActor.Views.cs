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

            var kernelSnapshot = await BuildKernelFactsSnapshotCoreAsync(flowProjection, ct).ConfigureAwait(false);
            return _policyVm.ProjectViews(
                _policy,
                _track.TemplateInputs,
                _track.Data,
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
        long? expectedDataRev = null) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
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
                if (TryGetActiveOwningTaskCore(caller.Id) is { Access: AccessMode.ReadOnly })
                    throw KernelException.PolicyDenied(
                        "Read-only bound worker sessions cannot patch track template data.");
            }

            if (expectedDataRev is { } expected && expected != _track.DataRev)
                throw KernelException.Conflict($"Data revision conflict: expected {expected}, current {_track.DataRev}.");

            var knownTaskKeys = _tasks.Select(t => t.TaskKey).Distinct(StringComparer.Ordinal).ToArray();
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
                await _store.UpdateTrackAndEmitAsync(nextTrack, evt, ct, writeData: true).ConfigureAwait(false);
                _track = nextTrack;
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
            }

            return new TrackDataPatchResult(true, _track.DataRev, _track.Data);
        }, ct);

    private Task<JsonElement> BuildKernelFactsSnapshotCoreAsync(FlowProjectionDto flowProjection, CancellationToken ct)
    {
        var tasksMap = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sessionsMap = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var kv in _sessions)
        {
            var s = kv.Value;
            sessionsMap[s.Id] = new
            {
                id = s.Id,
                isLead = s.IsLead,
                provider = s.Provider,
                model = s.Model,
                status = s.Status,
            };
        }

        foreach (var task in _tasks)
        {
            var boundSession = task.SessionId is { } sid && _sessions.TryGetValue(sid, out var session) ? session : null;
            var gateChecks = new List<object>();
            {
                if (task.GateVerdict is { ValueKind: JsonValueKind.Object } gv &&
                    gv.TryGetProperty("steps", out var stepsEl) &&
                    stepsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var step in stepsEl.EnumerateArray())
                    {
                        var name = step.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : "gate";
                        var exitCode = step.TryGetProperty("exitCode", out var ecEl) && ecEl.TryGetInt32(out var ec) ? ec : 0;
                        var passed = step.TryGetProperty("passed", out var pEl) && pEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? pEl.GetBoolean()
                            : exitCode == 0;
                        var durationMs = step.TryGetProperty("durationMs", out var dEl) && dEl.TryGetInt64(out var dm) ? dm : 0;
                        var cmd = step.TryGetProperty("cmd", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;
                        var tail = step.TryGetProperty("outputTail", out var oEl) && oEl.ValueKind == JsonValueKind.String ? oEl.GetString() : null;
                        var stdout = step.TryGetProperty("stdout", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
                        gateChecks.Add(new
                        {
                            label = name ?? "gate",
                            status = passed ? "pass" : "fail",
                            detail = durationMs > 0 ? $"{durationMs} ms" : null,
                            command = cmd,
                            output = string.IsNullOrWhiteSpace(tail) ? stdout : tail,
                        });
                    }
                }
            }

            var normalizedStatus = task.Status switch
            {
                TaskRunStatus.Done => "done",
                TaskRunStatus.Running or TaskRunStatus.Verifying => "running",
                TaskRunStatus.Failed or TaskRunStatus.Canceled => "blocked",
                _ => "pending"
            };

            tasksMap[task.TaskKey] = new
            {
                id = task.Id,
                taskKey = task.TaskKey,
                status = normalizedStatus,
                provider = boundSession?.Provider ?? task.Provider,
                model = boundSession?.Model,
                sessionId = boundSession?.Id,
                gates = gateChecks,
                updatedAtMs = task.UpdatedAtMs,
            };
        }

        return Task.FromResult(JsonHelpers.ToElement(new
        {
            trackId = TrackId,
            tasks = tasksMap,
            sessions = sessionsMap,
            flow = flowProjection,
        }));
    }
}
