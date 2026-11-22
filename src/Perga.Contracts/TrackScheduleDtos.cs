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
