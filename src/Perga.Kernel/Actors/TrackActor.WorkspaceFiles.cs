using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    internal async Task<WorkspaceFileOpenResultDto> RequestWorkspaceFileOpenAsync(
        InteropPrincipal.Session caller, string path, int? line, int? endLine, CancellationToken ct)
    {
        if (caller.BoundTaskId is not null)
            throw KernelException.Forbidden("Opening a file in the Track viewer requires an unbound session.");

        var env = _executionEnvironments.GetRequired(await GetExecutionTargetIdAsync(ct).ConfigureAwait(false));
        var mounted = await EnsureMountedAsync(ct).ConfigureAwait(false);
        var resolvedPath = await env.Files.ValidateWorkspaceFileAsync(mounted.MergedCwd, path, ct).ConfigureAwait(false);

        return await EnqueueAsync(_ =>
        {
            ct.ThrowIfCancellationRequested();
            RequireGovernanceSessionCore(caller.SessionId, KernelMcpToolNames.FileOpen);
            if (caller.RuntimeBindingId is not null)
                RequireRuntimeBindingCore(caller);
            if (_track.ClosedAtMs is not null || _closingTrack || _deletingTrack ||
                _mountedWorkspace?.MergedCwd != mounted.MergedCwd)
                throw KernelException.Conflict("The Track workspace changed before the file could be opened.");

            var request = new WorkspaceFileOpenRequestedDto(TrackId, caller.SessionId, resolvedPath, line, endLine);
            _eventHub.PublishTransient(_track.AreaId, TrackId, KernelEventKinds.WorkspaceFileOpenRequested,
                JsonHelpers.ToElement(request), NowMs());
            return Task.FromResult(new WorkspaceFileOpenResultDto(resolvedPath, line, endLine, Opened: true));
        }, ct).ConfigureAwait(false);
    }
}
