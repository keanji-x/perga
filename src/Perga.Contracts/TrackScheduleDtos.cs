namespace Perga.Contracts;

public sealed record TrackScheduleDto(
    string Id,
    string TrackId,
    string? TargetSessionId,
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
    string? CoalesceKey,
    string State,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record SetTrackScheduleRequest(
    string Title,
    string Prompt,
    string? Id = null,
    string? CronExpr = null,
    long? RunAtMs = null,
    string? Timezone = null,
    int? MaxOccurrences = null,
    string? TargetSessionId = null,
    string? SourceKind = null,
    string? CoalesceKey = null,
    string? RunAt = null);
