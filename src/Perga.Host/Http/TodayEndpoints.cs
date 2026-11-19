using System.Text.Json;
using Perga.Kernel;

namespace Perga.Host.Http;

public static class TodayEndpoints
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static IEndpointRouteBuilder MapTodayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var today = endpoints.MapGroup("/api/today");
        today.MapGet("/daily", async (string? date, IPergaKernel kernel, CancellationToken ct) =>
        {
            var daily = await kernel.GetDailyTrackAsync(date, ct).ConfigureAwait(false);
            return daily is null ? Results.Text("null", "application/json") : Results.Json(daily, WireOptions);
        });
        today.MapGet("/calendar", async (string? start, string? end, IPergaKernel kernel, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
                throw KernelException.InvalidArgument("start and end must be YYYY-MM-DD.");
            return Results.Json(await kernel.GetTodayCalendarAsync(start, end, ct).ConfigureAwait(false), WireOptions);
        });
        today.MapGet("/schedules", async (string? date, IPergaKernel kernel, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(date))
                throw KernelException.InvalidArgument("date must be YYYY-MM-DD.");
            return Results.Json(await kernel.GetTodaySchedulesAsync(date, ct).ConfigureAwait(false), WireOptions);
        });
        today.MapGet("/report-changes", async (string date, string? cursor, string? through_event_id,
            TodayReportService reports, CancellationToken ct) => Results.Json(
                await reports.ChangesAsync(date, cursor, through_event_id is null ? null : TodayReportService.ParseEventId(through_event_id), ct).ConfigureAwait(false), WireOptions));
        today.MapGet("/report-edits", async (string date, string track_id, string through_event_id,
            string? cursor, TodayReportService reports, CancellationToken ct) => Results.Json(
                await reports.EditsAsync(date, track_id, TodayReportService.ParseEventId(through_event_id), cursor, ct).ConfigureAwait(false), WireOptions));
        return endpoints;
    }
}
