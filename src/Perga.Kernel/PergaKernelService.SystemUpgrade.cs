using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    private void PublishUpgradeStatus()
    {
        _eventHub.PublishTransient(null, null, "system.upgrade_status",
            JsonHelpers.ToElement(new SystemUpgradeStatusPayload(DispatchAdmission.Draining, DispatchAdmission.ActiveDispatches)), NowMs());
    }

    private void ResumeSchedulers() => _ = ResumeSchedulersAsync();

    private async Task ResumeSchedulersAsync()
    {
        try
        {
            foreach (var track in await _store.ListTracksAsync(includeClosed: false, ct: CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    await Actors.GetOrActivate(track.Id).ResumeSchedulingAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning("Resume scheduler {0}: {1}", track.Id, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Resume schedulers: {0}", ex.Message);
        }
    }

    public async Task<JsonElement> InvokeSystemUpgradeAsync(McpSessionPrincipal caller, string operation, JsonElement arguments, CancellationToken ct)
    {
        // Caller role flags and BoundTaskId are insufficient: free subagents are unbound too.
        var track = await _store.GetTrackAsync(caller.TrackId, ct).ConfigureAwait(false);
        var aggregate = await _store.LoadTrackAggregateAsync(caller.TrackId, ct).ConfigureAwait(false);
        var session = aggregate?.Sessions.FirstOrDefault(s => s.Id == caller.SessionId);
        var ownsTask = aggregate?.Tasks.Any(t => t.SessionId == caller.SessionId) == true;
        if (track is null || track.ClosedAtMs is not null || track.LeadSessionId != caller.SessionId ||
            caller.BoundTaskId is not null || session is null || !session.IsLead || ownsTask || session.ArchivedAtMs is not null)
            throw new KernelException(KernelErrorCode.SystemUpgradeLeadRequired, "System upgrades require the persisted current Lead session.");
        if (operation is not ("check" or "apply" or "rollback") ||
            arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(p => p.Name != "sourceId") ||
            (arguments.TryGetProperty("sourceId", out var source) && (source.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(source.GetString()))) ||
            (operation == "rollback" && arguments.EnumerateObject().Any()))
            throw KernelException.InvalidArgument("Only a configured sourceId is accepted; rollback accepts no options.");
        var sourceId = arguments.TryGetProperty("sourceId", out var sourceValue) ? sourceValue.GetString() : null;
        if (operation == "apply" && sourceId is null)
        {
            throw KernelException.InvalidArgument("sourceId is required.");
        }
        // Keep this guard at the shared entry point for direct and dispatcher calls.
        var toolName = "system_upgrade_" + operation;
        var generation = await GuardMcpToolCallAsync(caller, "perga", toolName,
            JsonHelpers.ToElement(sourceId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["sourceId"] = sourceId }), ct).ConfigureAwait(false);
        if (_systemUpgradeController is null)
        {
            throw KernelException.Conflict("System upgrades are not configured.");
        }
        await EnsureMcpAuthorizationAsync(caller, generation, "perga", toolName, ct).ConfigureAwait(false);
        if (operation == "check")
        {
            return await _systemUpgradeController.CheckAsync(sourceId, ct).ConfigureAwait(false);
        }
        return JsonHelpers.ToElement(await _systemUpgradeController.ScheduleAsync(operation == "rollback", sourceId, track.Id, ct).ConfigureAwait(false));
    }
}
