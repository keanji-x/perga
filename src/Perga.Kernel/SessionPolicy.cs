using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public abstract record InteropPrincipal
{
    public sealed record Human : InteropPrincipal;
    public sealed record Session(string TrackId, string SessionId, bool IsLead, string? BoundAttemptId,
        int SpawnDepth, string Token, McpExecutionOrigin? ExecutionOrigin) : InteropPrincipal;
}

public static class SessionPolicy
{
    public const int MaxSessionSpawnDepth = 2;

    public static void RequireAccess(InteropPrincipal principal, AgentSessionDto target,
        AgentSessionDto? caller, bool write, bool activeTurn = false, bool destructive = false)
    {
        if (principal is InteropPrincipal.Human) return;
        if (caller is null || caller.TrackId != target.TrackId || caller.ArchivedAtMs is not null)
            throw KernelException.Forbidden("The session is outside the caller's ownership tree.");
        var self = caller.Id == target.Id;
        if (self && write && (activeTurn || destructive))
            throw KernelException.Forbidden("A session cannot modify or interrupt its own execution.");
        if ((!write && self) || target.ParentSessionId == caller.Id || caller.IsLead && !self) return;
        throw KernelException.Forbidden("The session is outside the caller's ownership tree.");
    }

    public static void RequireCreate(InteropPrincipal principal, AgentSessionDto? caller, string trackId,
        int maxDepth = MaxSessionSpawnDepth)
    {
        if (principal is InteropPrincipal.Human) return;
        if (caller is null || caller.TrackId != trackId || caller.ArchivedAtMs is not null || caller.BoundAttemptId is not null)
            throw KernelException.Forbidden("Only an active independent session may create children.");
        if (caller.SpawnDepth >= maxDepth)
            throw KernelException.Forbidden("The maximum session spawn depth has been reached.");
    }
}
