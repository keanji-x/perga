using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.ReportDoc;

namespace Perga.Host.Http;

public sealed record ApiErrorResponse(
    string Code,
    string Error,
    string? NextStep = null);

public static class HttpErrorHandling
{
    public static IResult BadRequest(string code, string error, string? nextStep = null) =>
        Results.Json(new ApiErrorResponse(code, error, nextStep), PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status400BadRequest);

    public static IResult Unauthorized(string error = "Missing or invalid authorization token.") =>
        Results.Json(new ApiErrorResponse("unauthorized", error, null), PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status401Unauthorized);

    public static IResult Forbidden(string code, string error, string? nextStep = null) =>
        Results.Json(new ApiErrorResponse(code, error, nextStep), PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status403Forbidden);

    public static IResult NotFound(string error) =>
        Results.Json(new ApiErrorResponse("not_found", error, null), PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status404NotFound);

    public static IResult MapException(Exception ex)
    {
        return ex switch
        {
            ReportConflictException conflict => Results.Json(
                new ApiErrorResponse(conflict.Code, conflict.Message, conflict.NextStep),
                PergaJsonDefaults.ApiOptions,
                statusCode: StatusCodes.Status409Conflict),

            KernelException kex => MapKernelException(kex),

            BadHttpRequestException { StatusCode: < 500 } badHttp => Results.Json(
                new ApiErrorResponse("bad_request", badHttp.Message, null),
                PergaJsonDefaults.ApiOptions,
                statusCode: badHttp.StatusCode),

            JsonException jsonEx => Results.Json(
                new ApiErrorResponse("invalid_json", jsonEx.Message, null),
                PergaJsonDefaults.ApiOptions,
                statusCode: StatusCodes.Status400BadRequest),

            KeyNotFoundException keyEx => Results.Json(
                new ApiErrorResponse("invalid_argument", keyEx.Message, null),
                PergaJsonDefaults.ApiOptions,
                statusCode: StatusCodes.Status400BadRequest),

            ArgumentException argEx => Results.Json(
                new ApiErrorResponse("invalid_argument", argEx.Message, null),
                PergaJsonDefaults.ApiOptions,
                statusCode: StatusCodes.Status400BadRequest),

            _ => Results.Json(
                new ApiErrorResponse("internal_error", "An unexpected server error occurred.", null),
                PergaJsonDefaults.ApiOptions,
                statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    public static async Task InvokeExceptionShieldMiddlewareAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client aborted the HTTP connection.
        }
        catch (Exception ex)
        {
            var mapped = MapException(ex);
            if (context.Response.HasStarted || mapped is IStatusCodeHttpResult { StatusCode: >= 500 })
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HttpErrorHandling).FullName!)
                    .LogError(ex, "Unhandled exception processing {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;

            context.Response.Headers.Remove("Set-Cookie");
            context.Response.Headers.Remove("X-Perga-Session-Id");
            await mapped.ExecuteAsync(context).ConfigureAwait(false);
        }
    }

    private static IResult MapKernelException(KernelException kex)
    {
        var (statusCode, code) = kex.Code switch
        {
            KernelErrorCode.IdempotencyKeyInvalid => (StatusCodes.Status400BadRequest, "idempotency_key_invalid"),
            KernelErrorCode.IdempotencyKeyReused => (StatusCodes.Status409Conflict, "idempotency_key_reused"),
            KernelErrorCode.IdempotencyKeyExhausted => (StatusCodes.Status410Gone, "idempotency_key_exhausted"),
            KernelErrorCode.Gone => (StatusCodes.Status410Gone, "gone"),
            KernelErrorCode.NotFound => (StatusCodes.Status404NotFound, "not_found"),
            KernelErrorCode.InvalidArgument => (StatusCodes.Status400BadRequest, "invalid_argument"),
            KernelErrorCode.SystemUpgradeLeadRequired => (StatusCodes.Status403Forbidden, "system_upgrade_lead_required"),
            KernelErrorCode.Forbidden => (StatusCodes.Status403Forbidden, "forbidden"),
            KernelErrorCode.PolicyDenied => (StatusCodes.Status403Forbidden, "policy_denied"),
            KernelErrorCode.Conflict => (StatusCodes.Status409Conflict, "conflict"),
            KernelErrorCode.WorkerTurnChanged => (StatusCodes.Status409Conflict, "worker_turn_changed"),
            KernelErrorCode.SessionRecoveryRequired => (StatusCodes.Status409Conflict, "session_recovery_required"),
            KernelErrorCode.PreconditionFailed => (StatusCodes.Status409Conflict, "precondition_failed"),
            _ => (StatusCodes.Status400BadRequest, "kernel_error")
        };

        return Results.Json(
            new ApiErrorResponse(code, kex.Message, kex.NextStep),
            PergaJsonDefaults.ApiOptions,
            statusCode: statusCode);
    }
}
