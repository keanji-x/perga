using Perga.Contracts;

namespace Perga.Kernel.Ports;

public static class TrackScheduleStates
{
    public const string Active = "active";
    public const string Done = "done";
    public const string Canceled = "canceled";
}

public static class TrackScheduleKinds
{
    public const string Once = "once";
    public const string Cron = "cron";
}

public static class TrackScheduleSources
{
    public const string Agent = "agent";
    public const string User = "user";
    public const string Plugin = "plugin";
    public const string Template = "template";
}

public sealed record FiredTrackSchedule(string TrackId, string ScheduleId);

public interface ITrackScheduleStore
{
    Task<bool> TriggerAsync(string trackId, string scheduleId, CancellationToken ct);
    Task<TrackScheduleDto> UpsertAsync(TrackScheduleDto schedule, CancellationToken ct);
    Task<TrackScheduleDto?> GetAsync(string trackId, string scheduleId, CancellationToken ct);
    Task<IReadOnlyList<TrackScheduleDto>> ListForTrackAsync(string trackId, bool includeTerminal, CancellationToken ct);
    Task<bool> CancelAsync(string trackId, string scheduleId, CancellationToken ct);
    Task<int> CancelActiveForTrackAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<FiredTrackSchedule>> FireDueAsync(
        long nowMs,
        Func<TrackScheduleDto, long?> computeNextFireAtMs,
        CancellationToken ct);
}
