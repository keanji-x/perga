using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Perga.Host.Serialization;
using Perga.Host.WebSockets;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.TermClient;

namespace Perga.Host.Http;

public sealed record SendMessageHttpBody(
    string Content,
    bool? SteerIfRunning = null);

public sealed record SteerMessageHttpBody(string TurnId, string Content);

public static class ApiV1Endpoints
{
    public static IEndpointRouteBuilder MapPergaApiV1(this IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapGroup("/api/v1");

        // ------------------- System -------------------
        v1.MapGet("/bootstrap", async (IPergaKernel kernel, CancellationToken ct) =>
        {
            var dto = await kernel.GetBootstrapAsync(ct).ConfigureAwait(false);
            return Results.Json(dto, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/templates", (IPergaKernel kernel) =>
        {
            var templates = kernel.ListTemplates();
            return Results.Json(templates, PergaJsonDefaults.ApiOptions);
        });

        // ------------------- Areas -------------------
        v1.MapGet("/areas", async (IPergaKernel kernel, CancellationToken ct) =>
        {
            var areas = await kernel.ListAreasAsync(ct).ConfigureAwait(false);
            return Results.Json(areas, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/areas", async (HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<CreateAreaRequest>(ctx, ct).ConfigureAwait(false);
            var created = await kernel.CreateAreaAsync(req, ct, OptionalCreationKey(ctx)).ConfigureAwait(false);
            return Results.Json(created, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status201Created);
        });

        v1.MapPatch("/areas/{areaId}", async (string areaId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<PatchAreaRequest>(ctx, ct).ConfigureAwait(false);
            var updated = await kernel.PatchAreaAsync(areaId, req, ct).ConfigureAwait(false);
            return Results.Json(updated, PergaJsonDefaults.ApiOptions);
        });

        v1.MapDelete("/areas/{areaId}", async (string areaId, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.DeleteAreaAsync(areaId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // ------------------- Tracks -------------------
        v1.MapGet("/areas/{areaId}/tracks", async (string areaId, bool? includeClosed, IPergaKernel kernel, CancellationToken ct) =>
        {
            _ = await kernel.GetAreaAsync(areaId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound($"Area '{areaId}' not found.");
            var tracks = await kernel.ListTracksAsync(areaId, includeClosed ?? false, ct).ConfigureAwait(false);
            return Results.Json(tracks, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/tracks", async (HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<CreateTrackRequest>(ctx, ct).ConfigureAwait(false);
            var snapshot = await kernel.CreateTrackAsync(req, ct, OptionalCreationKey(ctx)).ConfigureAwait(false);
            return Results.Json(snapshot, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status201Created);
        });

        v1.MapGet("/tracks/{trackId}", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var snapshot = await kernel.GetTrackAggregateAsync(trackId, ct).ConfigureAwait(false);
            return Results.Json(snapshot, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/tracks/{trackId}/workspace/retry", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.RetryWorkspaceAsync(trackId, ct).ConfigureAwait(false);
            return Results.Accepted();
        });

        v1.MapPatch("/tracks/{trackId}", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<PatchTrackRequest>(ctx, ct).ConfigureAwait(false);
            var updated = await kernel.PatchTrackAsync(trackId, req, ct).ConfigureAwait(false);
            return Results.Json(updated, PergaJsonDefaults.ApiOptions);
        });

        v1.MapDelete("/tracks/{trackId}", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.DeleteTrackAsync(trackId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // ------------------- Workspace & VCS -------------------
        v1.MapGet("/tracks/{trackId}/files", async (string trackId, string? path, int? maxBytes, IPergaKernel kernel, CancellationToken ct) =>
        {
            var result = await kernel.BrowseOrReadWorkspaceFileAsync(
                trackId,
                path,
                maxBytes ?? (256 * 1024),
                ct).ConfigureAwait(false);
            return Results.Json(result, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/tracks/{trackId}/vcs-diff", async (string trackId, string? fromRev, string? toRev, IPergaKernel kernel, CancellationToken ct) =>
        {
            var diff = await kernel.GetTrackVcsDiffAsync(trackId, fromRev, toRev, ct).ConfigureAwait(false);
            return Results.Json(diff, PergaJsonDefaults.ApiOptions);
        });

        // ------------------- Report & Tasks -------------------
        v1.MapGet("/tracks/{trackId}/report", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var report = await kernel.ReadReportAsync(trackId, callerSessionId: null, ct).ConfigureAwait(false);
            return Results.Json(report, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPut("/tracks/{trackId}/report/blocks/{blockId}", async (string trackId, string blockId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<HumanBlockUpdateRequest>(ctx, ct).ConfigureAwait(false);
            var updated = await kernel.UpdateReportBlockByHumanAsync(trackId, blockId, req, ct).ConfigureAwait(false);
            return Results.Json(updated, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/tracks/{trackId}/tasks/{taskKey}/attempts", async (string trackId, string taskKey, IPergaKernel kernel, CancellationToken ct) =>
        {
            var attempts = await kernel.ListTaskAttemptsAsync(trackId, taskKey, ct).ConfigureAwait(false);
            return Results.Json(attempts, PergaJsonDefaults.ApiOptions);
        });

        // ------------------- Sessions -------------------
        v1.MapGet("/tracks/{trackId}/sessions", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var sessions = await kernel.ListSessionsAsync(trackId, ct).ConfigureAwait(false);
            return Results.Json(sessions, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/tracks/{trackId}/sessions", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var key) ||
                string.IsNullOrWhiteSpace(key.ToString()))
                return HttpErrorHandling.BadRequest("missing_idempotency_key", "Idempotency-Key HTTP header is required.");
            if (key.Count != 1)
                return HttpErrorHandling.BadRequest("invalid_argument", "Exactly one Idempotency-Key is allowed.");

            var req = await ReadOptionalJsonBodyAsync(ctx, () => new CreateSessionRequest(), ct).ConfigureAwait(false);
            var (receipt, replay) = await kernel.CreateSessionWithReceiptAsync(trackId, req, key.ToString().Trim(), ct)
                .ConfigureAwait(false);
            return Results.Json(receipt, PergaJsonDefaults.ApiOptions,
                statusCode: replay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        });

        v1.MapGet("/sessions/{sessionId}/items", async (string sessionId, long? afterId, int? limit, bool? latest, long? beforeId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var page = await kernel.GetSessionItemsAsync(
                sessionId,
                afterId ?? 0,
                limit ?? 100,
                ct, latest ?? false, beforeId).ConfigureAwait(false);
            return Results.Json(page, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/messages", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var idemValues) ||
                string.IsNullOrWhiteSpace(idemValues.ToString()))
            {
                return HttpErrorHandling.BadRequest(
                    "missing_idempotency_key",
                    "Idempotency-Key HTTP header is required.");
            }

            if (idemValues.Count != 1)
                return HttpErrorHandling.BadRequest("invalid_argument", "Exactly one Idempotency-Key is allowed.");

            var idempotencyKey = idemValues.ToString().Trim();
            var body = await ReadRequiredJsonBodyAsync<SendMessageHttpBody>(ctx, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body.Content))
            {
                return HttpErrorHandling.BadRequest("invalid_argument", "Message content cannot be empty.");
            }

            var (receipt, steered, replay) = await kernel.AcceptMessageAsync(
                sessionId, new SendMessageRequest(body.Content, idempotencyKey, body.SteerIfRunning ?? false), ct)
                .ConfigureAwait(false);
            if (receipt is null)
                return Results.Json(steered, PergaJsonDefaults.ApiOptions);

            ctx.Response.Headers.Location = receipt.StatusUrl;
            return Results.Json(receipt, PergaJsonDefaults.ApiOptions,
                statusCode: replay ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        });

        v1.MapPost("/sessions/{sessionId}/messages/steer", async (
            string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadRequiredJsonBodyAsync<SteerMessageHttpBody>(ctx, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body.Content) || string.IsNullOrWhiteSpace(body.TurnId))
                return HttpErrorHandling.BadRequest("invalid_argument", "A target turn and message are required.");

            var result = await kernel.SteerMessageAsync(sessionId, body.TurnId, body.Content, ct).ConfigureAwait(false);
            return Results.Json(result, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/sessions/{sessionId}/inputs/{inputId}", async (
            string sessionId, string inputId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var receipt = await kernel.GetSessionInputAsync(sessionId, inputId, ct).ConfigureAwait(false);
            return Results.Json(receipt, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/inputs/{inputId}/abandon", async (
            string sessionId, string inputId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var receipt = await kernel.ResolveUnknownInputAsync(sessionId, inputId, ct).ConfigureAwait(false);
            return Results.Json(receipt, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/interrupt", async (string sessionId, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.InterruptSessionAsync(sessionId, ct).ConfigureAwait(false);
            return Results.Json(new { ok = true }, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/compact", async (string sessionId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var compacted = await kernel.CompactSessionAsync(sessionId, ct).ConfigureAwait(false);
            return Results.Json(compacted, PergaJsonDefaults.ApiOptions);
        });

        // ------------------- Asks -------------------
        v1.MapPost("/tracks/{trackId}/asks/{askId}/answer", async (string trackId, string askId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return HttpErrorHandling.BadRequest("invalid_argument", "Ask answer payload cannot be null.");
            }

            var answered = await kernel.AnswerAskAsync(trackId, askId, root.Clone(), ct).ConfigureAwait(false);
            return Results.Json(answered, PergaJsonDefaults.ApiOptions);
        });

        // ------------------- Terminals -------------------
        v1.MapGet("/tracks/{trackId}/terminals", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var terminals = await kernel.ListTerminalsAsync(trackId, ct).ConfigureAwait(false);
            return Results.Json(terminals, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/tracks/{trackId}/terminals", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadOptionalJsonBodyAsync(ctx, () => new OpenTerminalRequest(), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(req.IdempotencyKey) &&
                ctx.Request.Headers.TryGetValue("Idempotency-Key", out var idemHeader) &&
                !string.IsNullOrWhiteSpace(idemHeader.ToString()))
            {
                req = req with { IdempotencyKey = idemHeader.ToString().Trim() };
            }

            var created = await kernel.OpenTerminalAsync(trackId, callerSessionId: null, req, ct).ConfigureAwait(false);
            return Results.Json(created, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status201Created);
        });

        v1.MapDelete("/tracks/{trackId}/terminals/{termId}", async (string trackId, string termId, IPergaKernel kernel, CancellationToken ct) =>
        {
            _ = await kernel.CloseTerminalAsync(trackId, termId, graceMs: 2000, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // ------------------- WebSockets -------------------
        v1.Map("/events", (HttpContext ctx, IPergaKernel kernel, ITrackReadStore readStore) =>
            EventsWebSocketHandler.HandleAsync(ctx, kernel, readStore));

        v1.Map("/terminals/{termId}/ws", (string termId, HttpContext ctx, ITerminalBridgeClient terminalClient) =>
            TerminalWebSocketRelayHandler.HandleAsync(ctx, termId, terminalClient));

        return endpoints;
    }

    private static string? OptionalCreationKey(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var values))
            return null;
        if (values.Count != 1)
            throw KernelException.InvalidArgument("Exactly one Idempotency-Key is allowed.");
        return values[0] ?? string.Empty;
    }

    private static async Task<T> ReadRequiredJsonBodyAsync<T>(HttpContext ctx, CancellationToken ct)
    {
        var value = await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, PergaJsonDefaults.ApiOptions, ct)
            .ConfigureAwait(false);
        if (value is null)
        {
            throw KernelException.InvalidArgument("Request JSON body cannot be empty.");
        }

        return value;
    }

    private static async Task<T> ReadOptionalJsonBodyAsync<T>(HttpContext ctx, Func<T> factory, CancellationToken ct)
    {
        if (ctx.Request.ContentLength == 0)
        {
            return factory();
        }

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
        if (ms.Length == 0)
        {
            return factory();
        }

        var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        var hasNonWhitespace = false;
        foreach (var b in span)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                hasNonWhitespace = true;
                break;
            }
        }

        if (!hasNonWhitespace)
        {
            return factory();
        }

        ms.Position = 0;
        var parsed = await JsonSerializer.DeserializeAsync<T>(ms, PergaJsonDefaults.ApiOptions, ct)
            .ConfigureAwait(false);
        return parsed ?? factory();
    }
}
