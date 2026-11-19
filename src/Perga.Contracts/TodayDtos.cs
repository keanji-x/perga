using System.Text.Json.Serialization;

namespace Perga.Contracts;

public sealed record DailyTrackDto(string Date,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    [property: JsonPropertyName("track_id")] string TrackId);

public sealed record DailyReportEditDto(string TrackId, string EditId, string SummaryBefore,
    string SummaryAfter, string BodyBefore, string BodyAfter);
public sealed record DailyReportEditEntryDto(long EventId, long At, DailyReportEditDto Edit);
public sealed record DailyReportEditsPageDto(IReadOnlyList<DailyReportEditEntryDto> Edits, string? NextCursor);
public sealed record DailyReportChangeDto(string TrackId, string TrackTitle, string AreaId, string AreaName,
    int EditCount, long FirstEventId, long LastEventId, string SummaryBefore, string SummaryAfter,
    string Patch, bool PatchTruncated);
public sealed record DailyReportChangesPageDto(string Date, string Timezone, long ThroughEventId,
    IReadOnlyList<DailyReportChangeDto> Changes, string? NextCursor);

public sealed record TodayCalendarDayDto(
    string Date,
    bool HasDailyTrack,
    bool HasReport,
    int ScheduleCount);

public sealed record TodayCalendarDto(
    string Today,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    string Start,
    string End,
    IReadOnlyList<TodayCalendarDayDto> Days);

public sealed record TodayScheduleItemDto(
    string Id,
    string TrackId,
    string TrackTitle,
    string Title,
    string ScheduleKind,
    string? CronExpr,
    string Timezone,
    long NextFireAtMs,
    string State);

public sealed record TodaySchedulesDto(
    string Date,
    [property: JsonPropertyName("time_zone")] string TimeZone,
    IReadOnlyList<TodayScheduleItemDto> Schedules);
