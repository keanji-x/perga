namespace Perga.Contracts;

public sealed record TrackScheduleDto(
    string Id,
    string TrackId,
    string SourceKind,
    string ScheduleKind,
    string Title,
    string Prompt,
    string? CronExpr,
    string Timezone,
    long? RunAtMs,
    long? NextFireAtMs,
    long? LastFiredAtMs,
    int? MaxOccurrences,
    int FiredCount,
    string State,
    long CreatedAtMs,
    long UpdatedAtMs,
    string? NodeKey = null);

public sealed record SetTrackScheduleRequest(
    string Title,
    string Prompt,
    string? Id = null,
    string? CronExpr = null,
    long? RunAtMs = null,
    string? Timezone = null,
    int? MaxOccurrences = null,
    string? SourceKind = null,
    string? RunAt = null);

public sealed record TrackScheduleRunDto(
    string WakeId,
    string? ScheduleId,
    string State,
    string? Reason,
    string? TurnId,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record TrackSchedulesSnapshot(
    IReadOnlyList<TrackScheduleDto> Schedules,
    IReadOnlyList<TrackScheduleRunDto> RecentRuns);
