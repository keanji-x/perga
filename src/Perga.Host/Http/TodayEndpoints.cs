using Perga.Host.Auth;
using Perga.Contracts;
using Perga.Host.Serialization;
using Perga.Kernel;

namespace Perga.Host.Http;

public static class TodayEndpoints
{
    public static IEndpointRouteBuilder MapTodayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var today = endpoints.MapGroup("/api/v1/today").RequireAuthorization(PergaAuthPolicies.UserSession)
            .WithMetadata(new RequestOriginGuard.BrowserWriteApi());
        today.MapGet("/daily", async (string? date, IPergaKernel kernel, CancellationToken ct) =>
        {
            var daily = await kernel.GetDailyTrackAsync(date, ct).ConfigureAwait(false);
            return daily is null ? Results.Text("null", "application/json") : TypedResults.Json(daily, PergaJsonDefaults.ApiOptions);
        }).Produces<DailyTrackDto>().WithMetadata(new NullableResponse());
        today.MapGet("/calendar", async (string? start, string? end, IPergaKernel kernel, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
                throw KernelException.InvalidArgument("start and end must be YYYY-MM-DD.");
            return TypedResults.Json(await kernel.GetTodayCalendarAsync(start, end, ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions);
        });
        today.MapGet("/report-changes", async (string date, string? cursor, string? throughEventId,
            TodayReportService reports, CancellationToken ct) => TypedResults.Json(
                await reports.ChangesAsync(date, cursor, throughEventId is null ? null : TodayReportService.ParseEventId(throughEventId), ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions));
        today.MapGet("/report-edits", async (string date, string trackId, string throughEventId,
            string? cursor, TodayReportService reports, CancellationToken ct) => TypedResults.Json(
                await reports.EditsAsync(date, trackId, TodayReportService.ParseEventId(throughEventId), cursor, ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions));
        return endpoints;
    }
}
