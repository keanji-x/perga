using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Perga.Contracts;
using Perga.Host.Serialization;
using Perga.Host.WebSockets;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.TermClient;

namespace Perga.Host.Http;

public sealed record CancelTaskHttpBody(
    string? Reason = null);

public sealed record AcceptTaskHttpBody(
    string? Note = null);

public sealed record RejectTaskHttpBody(
    string? Note = null,
    bool? ReQueue = null);

public static class ApiV1Endpoints
{
    private static readonly JsonSerializerOptions SessionJsonOptions = new(PergaJsonDefaults.ApiOptions)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static readonly FileExtensionContentTypeProvider RawMimeProvider = CreateMimeProvider();

    public static IEndpointRouteBuilder MapPergaApiV1(this IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapGroup("/api/v1");

        v1.MapGet("/read-receipts", async (IPergaKernel kernel, CancellationToken ct) =>
            Results.Json(await kernel.GetReadReceiptsAsync(ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions));
        v1.MapPost("/read-receipts/{kind}/{targetId}/read", async (string kind, string targetId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<MarkReadRequest>(ctx, ct).ConfigureAwait(false);
            return Results.Json(await kernel.MarkReadAsync(kind, targetId, req.ReadThroughMs ?? 0, ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions);
        });
        v1.MapPost("/read-receipts/{kind}/{targetId}/unread", async (string kind, string targetId, IPergaKernel kernel, CancellationToken ct) =>
            Results.Json(await kernel.MarkUnreadAsync(kind, targetId, ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions));

        // Empty segments do not bind {targetId}; match only literal //read and //unread.
        v1.MapPost("/read-receipts/{kind}/{**emptyTarget:regex(^/(?:read|unread)$)}", () =>
        {
            throw KernelException.InvalidArgument("TargetId cannot be empty.");
        }).ExcludeFromDescription();

        // ------------------- System -------------------
        v1.MapGet("/bootstrap", async (string? scope, bool? refresh, IPergaKernel kernel, CancellationToken ct) =>
        {
            var dto = await kernel.GetBootstrapAsync(scope, refresh ?? false, ct).ConfigureAwait(false);
            return Results.Json(dto, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/overlays", async (bool? includeClosed, IPergaKernel kernel, CancellationToken ct) =>
        {
            var overlays = await kernel.GetOverlaysAsync(includeClosed ?? false, ct).ConfigureAwait(false);
            return Results.Json(overlays, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/fs/listdir", (string? path, IPergaKernel kernel) =>
        {
            var listing = kernel.ListHostDirectory(path);
            return Results.Json(listing, PergaJsonDefaults.ApiOptions);
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
        v1.MapGet("/areas/{areaId}/workspace-options", async (string areaId, string? cwd, string? templateId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var options = await kernel.GetTrackWorkspaceOptionsAsync(areaId, cwd, templateId, ct).ConfigureAwait(false);
            return Results.Json(options, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/areas/{areaId}/tracks", async (string areaId, bool? includeClosed, IPergaKernel kernel, CancellationToken ct) =>
        {
            _ = await kernel.GetAreaAsync(areaId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound($"Area '{areaId}' not found.");
            var tracks = await kernel.ListTracksAsync(areaId, includeClosed ?? false, ct).ConfigureAwait(false);
            return Results.Json(tracks, PergaJsonDefaults.ApiOptions);
        }).Produces<IReadOnlyList<TrackDto>>();

        v1.MapPost("/tracks", async (HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<CreateTrackRequest>(ctx, ct).ConfigureAwait(false);
            var snapshot = await kernel.CreateTrackAsync(req, ct, OptionalCreationKey(ctx)).ConfigureAwait(false);
            return Results.Json(snapshot, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status201Created);
        }).Produces<TrackAggregateSnapshot>(StatusCodes.Status201Created);

        v1.MapGet("/tracks/{trackId}", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var snapshot = await kernel.GetTrackAggregateAsync(trackId, ct).ConfigureAwait(false);
            return Results.Json(snapshot, PergaJsonDefaults.ApiOptions);
        }).Produces<TrackAggregateSnapshot>();

        v1.MapGet("/tracks/{trackId}/template", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var template = await kernel.Actors.GetOrActivate(trackId).InspectCurrentTemplateAsync(ct).ConfigureAwait(false);
            return Results.Json(template, PergaJsonDefaults.ApiOptions);
        }).Produces<JsonElement>();

        v1.MapDelete("/tracks/{trackId}/template", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var track = await kernel.Actors.GetOrActivate(trackId).ClearTemplateAsync(ct).ConfigureAwait(false);
            return Results.Json(track, PergaJsonDefaults.ApiOptions);
        }).Produces<TrackDto>();

        v1.MapPatch("/tracks/{trackId}/template/inputs", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var patch = await ReadRequiredJsonBodyAsync<JsonElement>(ctx, ct).ConfigureAwait(false);
            var track = await kernel.Actors.GetOrActivate(trackId).UpdateTemplateInputsAsync(patch, ct).ConfigureAwait(false);
            return Results.Json(track, PergaJsonDefaults.ApiOptions);
        }).Produces<TrackDto>();

        v1.MapGet("/tracks/{trackId}/schedules", async (string trackId, bool? includeTerminal, int? runsLimit, IPergaKernel kernel, CancellationToken ct) =>
            Results.Json(await kernel.GetTrackSchedulesAsync(trackId, includeTerminal ?? false, runsLimit ?? 20, ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions))
            .Produces<TrackSchedulesSnapshot>();

        v1.MapPost("/tracks/{trackId}/schedules", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<SetTrackScheduleRequest>(ctx, ct).ConfigureAwait(false);
            if (req.SourceKind is not null and not "user")
                throw KernelException.InvalidArgument("HTTP schedules must use sourceKind 'user'.");
            var schedule = await kernel.SetTrackScheduleAsync(trackId, req with { SourceKind = "user" }, ct).ConfigureAwait(false);
            return Results.Json(schedule, PergaJsonDefaults.ApiOptions);
        }).Produces<TrackScheduleDto>();

        v1.MapDelete("/tracks/{trackId}/schedules/{scheduleId}", async (string trackId, string scheduleId, IPergaKernel kernel, CancellationToken ct) =>
            await kernel.CancelTrackScheduleAsync(trackId, scheduleId, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound());

        v1.MapPost("/tracks/{trackId}/schedules/{scheduleId}/trigger", async (string trackId, string scheduleId, IPergaKernel kernel, CancellationToken ct) =>
            await kernel.TriggerTrackScheduleAsync(trackId, scheduleId, ct).ConfigureAwait(false) ? Results.Accepted() : Results.NotFound());

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
        }).Produces<TrackDto>();

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

        v1.MapGet("/tracks/{trackId}/files/raw", async (string trackId, string? path, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var (fullFilePath, stream) = await kernel.OpenWorkspaceRawFileAsync(trackId, path, ct).ConfigureAwait(false);
            if (!RawMimeProvider.TryGetContentType(fullFilePath, out var contentType) || string.IsNullOrWhiteSpace(contentType))
            {
                contentType = "application/octet-stream";
            }

            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (RequiresSandboxCsp(fullFilePath, contentType))
            {
                ctx.Response.Headers["Content-Security-Policy"] = "sandbox";
            }

            return Results.File(stream, contentType, enableRangeProcessing: true);
        });

        v1.MapGet("/tracks/{trackId}/vcs-diff", async (string trackId, string? path, string? fromRev, string? toRev, IPergaKernel kernel, CancellationToken ct) =>
        {
            var diff = await kernel.GetTrackVcsDiffAsync(trackId, fromRev, toRev, path, ct).ConfigureAwait(false);
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

        v1.MapPost("/tracks/{trackId}/tasks/{taskKey}/cancel", async (string trackId, string taskKey, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadOptionalJsonBodyAsync(ctx, () => new CancelTaskHttpBody(), ct).ConfigureAwait(false);
            var task = await kernel.CancelTaskAsync(trackId, callerSessionId: null, taskKey, body.Reason, ct).ConfigureAwait(false);
            return Results.Json(task, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/tracks/{trackId}/tasks/{taskKey}/accept", async (string trackId, string taskKey, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadOptionalJsonBodyAsync(ctx, () => new AcceptTaskHttpBody(), ct).ConfigureAwait(false);
            var task = await kernel.AcceptTaskAsync(trackId, callerSessionId: null, taskKey, body.Note, ct).ConfigureAwait(false);
            return Results.Json(task, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/tracks/{trackId}/tasks/{taskKey}/reject", async (string trackId, string taskKey, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadOptionalJsonBodyAsync(ctx, () => new RejectTaskHttpBody(), ct).ConfigureAwait(false);
            var note = string.IsNullOrWhiteSpace(body.Note) ? "Rejected by user." : body.Note!;
            var task = await kernel.RejectTaskAsync(trackId, callerSessionId: null, taskKey, note, body.ReQueue ?? false, ct).ConfigureAwait(false);
            return Results.Json(task, PergaJsonDefaults.ApiOptions);
        });

        // ------------------- Unified sessions -------------------
        v1.MapGet("/tracks/{trackId}/sessions", async (string trackId, bool? includeArchived, string? parentSessionId,
            IPergaKernel kernel, CancellationToken ct) =>
            Results.Json(await kernel.ListSessionsAsync(trackId, new(includeArchived ?? false, parentSessionId),
                new InteropPrincipal.Human(), ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions));

        v1.MapPost("/tracks/{trackId}/sessions", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var key = RequiredMessageKey(ctx);
            var request = await ReadOptionalJsonBodyAsync(ctx, () => new CreateSessionRequest(), ct, SessionJsonOptions).ConfigureAwait(false);
            var (receipt, replay) = await kernel.CreateSessionAsync(trackId, request, key, new InteropPrincipal.Human(), ct).ConfigureAwait(false);
            return Results.Json(receipt, PergaJsonDefaults.ApiOptions,
                statusCode: replay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        });

        v1.MapGet("/sessions/{sessionId}/items", async (string sessionId, long? afterId, long? beforeId, int? limit,
            bool? latest, string? activityTurnId, long? afterSequence, int? waitMs, IPergaKernel kernel, CancellationToken ct) =>
        {
            if (limit is < 1 or > 100 || waitMs is < 0 or > 10000 || afterId is < 0 || beforeId is < 0 || afterSequence is < 0)
                throw KernelException.InvalidArgument("Invalid session read range.");
            return Results.Json(await kernel.GetSessionItemsAsync(sessionId,
                new(afterId ?? 0, beforeId, limit ?? 20, latest ?? false, activityTurnId, afterSequence, waitMs ?? 0),
                new InteropPrincipal.Human(), ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions);
        });

        v1.MapPatch("/sessions/{sessionId}", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var request = await ReadRequiredJsonBodyAsync<UpdateSessionRequest>(ctx, ct, SessionJsonOptions).ConfigureAwait(false);
            return Results.Json(await kernel.UpdateSessionAsync(sessionId, request, new InteropPrincipal.Human(), ct).ConfigureAwait(false),
                PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/messages", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var request = await ReadRequiredJsonBodyAsync<SendSessionMessageRequest>(ctx, ct, SessionJsonOptions).ConfigureAwait(false);
            request = request with { RequestKey = request.Content is not null ? RequiredMessageKey(ctx) : null };
            return Results.Json(await kernel.SendSessionMessageAsync(sessionId, request, new InteropPrincipal.Human(), ct).ConfigureAwait(false),
                PergaJsonDefaults.ApiOptions);
        });

        v1.MapDelete("/sessions/{sessionId}/messages/{messageId}", async (string sessionId, string messageId,
            string? expectedAttemptId, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.WithdrawSessionMessageAsync(sessionId, messageId, expectedAttemptId, new InteropPrincipal.Human(), ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        v1.MapPost("/sessions/{sessionId}/control", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var request = await ReadRequiredJsonBodyAsync<SessionControlRequest>(ctx, ct, SessionJsonOptions).ConfigureAwait(false);
            return Results.Json(await kernel.ControlSessionAsync(sessionId, request, new InteropPrincipal.Human(), ct).ConfigureAwait(false),
                PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/interactions/{interactionId}/respond", async (string sessionId, string interactionId,
            HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var request = await ReadRequiredJsonBodyAsync<RespondInteractionRequest>(ctx, ct, SessionJsonOptions).ConfigureAwait(false);
            return Results.Json(await kernel.RespondInteractionAsync(sessionId, interactionId, request, new InteropPrincipal.Human(), ct).ConfigureAwait(false),
                PergaJsonDefaults.ApiOptions);
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

    private static async Task<T> ReadRequiredJsonBodyAsync<T>(HttpContext ctx, CancellationToken ct, JsonSerializerOptions? options = null)
    {
        var value = await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, options ?? PergaJsonDefaults.ApiOptions, ct)
            .ConfigureAwait(false);
        if (value is null)
        {
            throw KernelException.InvalidArgument("Request JSON body cannot be empty.");
        }

        return value;
    }

    private static async Task<T> ReadOptionalJsonBodyAsync<T>(HttpContext ctx, Func<T> factory, CancellationToken ct, JsonSerializerOptions? options = null)
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
        var parsed = await JsonSerializer.DeserializeAsync<T>(ms, options ?? PergaJsonDefaults.ApiOptions, ct)
            .ConfigureAwait(false);
        return parsed ?? factory();
    }

    private static string RequiredMessageKey(HttpContext ctx)
    {
        var key = OptionalCreationKey(ctx);
        if (string.IsNullOrWhiteSpace(key))
            throw KernelException.InvalidArgument("Idempotency-Key HTTP header is required.");
        return key.Trim();
    }

    private static FileExtensionContentTypeProvider CreateMimeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".svg"] = "image/svg+xml";
        provider.Mappings[".webp"] = "image/webp";
        provider.Mappings[".avif"] = "image/avif";
        provider.Mappings[".md"] = "text/markdown; charset=utf-8";
        provider.Mappings[".ts"] = "text/plain; charset=utf-8";
        provider.Mappings[".tsx"] = "text/plain; charset=utf-8";
        provider.Mappings[".jsx"] = "text/plain; charset=utf-8";
        return provider;
    }

    private static bool RequiresSandboxCsp(string filePath, string contentType)
    {
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("svg", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ext = Path.GetExtension(filePath);
        return ext.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".xhtml", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

}
