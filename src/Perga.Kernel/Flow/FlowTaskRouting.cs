using System.Text.Json;
using Perga.Contracts;
using Perga.PolicyVm;

namespace Perga.Kernel.Flow;

internal static class FlowTaskRouting
{
    /// <summary>
    /// Host shell commands run in the writable workspace without a read-only sandbox fence,
    /// so omitted access stays read_write and cannot silently inherit a shared read_only lease.
    /// </summary>
    internal static string? RequestedAccess(FlowNodeSpecDto node) =>
        !string.IsNullOrWhiteSpace(node.Do.Cmd) ? (node.Do.Access ?? "read_write") : node.Do.Access;

    internal static PolicyTaskRoute Resolve(FlowNodeSpecDto node, CompiledPolicy policy, IPolicyVm vm,
        JsonElement inputs, AgentSessionDto? reusedSession) =>
        vm.RouteTaskDispatch(policy, node.Do.Role, RequestedAccess(node),
            reusedSession?.Provider ?? SessionLaunchValidator.NormalizeTaskProviderId(node.Do.Provider),
            reusedSession is null ? node.Do.Model : reusedSession.Model, inputs);

    internal static AccessMode? ResolveAccess(FlowNodeSpecDto node, CompiledPolicy? policy, IPolicyVm vm,
        JsonElement inputs, IReadOnlyDictionary<string, AgentSessionDto>? sessions)
    {
        if (node.Do.Kind != "run" || node.Canceled) return null;
        var requested = RequestedAccess(node);
        var session = node.Do.Session is { } sessionId ? sessions?.GetValueOrDefault(sessionId) : null;
        var access = policy is null ? requested : Resolve(node, policy, vm, inputs, session).Access ?? requested;
        return access switch
        {
            "read_only" => AccessMode.ReadOnly,
            _ => AccessMode.ReadWrite
        };
    }
}
