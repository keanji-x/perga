using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public abstract record InteropPrincipal
{
    public sealed record Human(string UserId = "local-owner", string Username = "local-owner") : InteropPrincipal;
    public sealed record Session(string TrackId, string SessionId, bool IsLead, string? BoundTaskId,
        int SpawnDepth, string Token, string? RuntimeBindingId) : InteropPrincipal
    {
        internal string? AuthorizedPolicyGeneration { get; init; }
    }
}

public static class SessionPolicy
{
    public const int MaxSessionSpawnDepth = 2;

    public static void RequireAccess(InteropPrincipal principal, AgentSessionDto target,
        AgentSessionDto? caller, bool write, bool activeTurn = false, bool destructive = false)
    {
        if (AccessError(principal, target, caller, write, activeTurn, destructive) is { } error)
            throw KernelException.Forbidden(error);
    }

    public static bool CanAccess(InteropPrincipal principal, AgentSessionDto target,
        AgentSessionDto? caller, bool write, bool activeTurn = false, bool destructive = false) =>
        AccessError(principal, target, caller, write, activeTurn, destructive) is null;

    private static string? AccessError(InteropPrincipal principal, AgentSessionDto target,
        AgentSessionDto? caller, bool write, bool activeTurn, bool destructive)
    {
        if (principal is InteropPrincipal.Human) return null;
        if (caller is null || caller.TrackId != target.TrackId || caller.ArchivedAtMs is not null)
            return "The session is outside the caller's ownership tree.";
        var self = caller.Id == target.Id;
        if (self && write && (activeTurn || destructive))
            return "A session cannot modify or interrupt its own execution.";
        if ((!write && self) || target.ParentSessionId == caller.Id || caller.IsLead && !self) return null;
        return "The session is outside the caller's ownership tree.";
    }

    public static void RequireCreate(InteropPrincipal principal, AgentSessionDto? caller, string trackId,
        bool callerIsTaskOwned = false, int maxDepth = MaxSessionSpawnDepth)
    {
        if (principal is InteropPrincipal.Human) return;
        if (caller is null || caller.TrackId != trackId || caller.ArchivedAtMs is not null ||
            callerIsTaskOwned || principal is InteropPrincipal.Session { BoundTaskId: not null })
            throw KernelException.Forbidden("Only an active independent session may create children.");
        if (caller.SpawnDepth >= maxDepth)
            throw KernelException.Forbidden("The maximum session spawn depth has been reached.");
    }
}
