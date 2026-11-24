using Perga.Contracts;

namespace Perga.Kernel.Ports;

/// <summary>Creates a Track and durably submits its initial Lead input for a Flow run.</summary>
public interface IFlowTrackSpawner
{
    Task<TrackDto> SpawnAsync(TrackDto parent, AgentSessionDto? parentLead, string taskId, int runCount, FlowDoSpecDto action, CancellationToken ct);
}
