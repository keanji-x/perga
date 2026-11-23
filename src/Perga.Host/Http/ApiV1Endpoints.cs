using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
        v1.MapGet("/bootstrap", async (string? scope, bool? refresh, string? card_id, IPergaKernel kernel, CancellationToken ct) =>
        {
            var dto = await kernel.GetBootstrapAsync(scope, refresh ?? false, ct, card_id).ConfigureAwait(false);
            return Results.Json(dto, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/overlays", async (bool? includeClosed, IPergaKernel kernel, CancellationToken ct) =>
        {
            var overlays = await kernel.GetOverlaysAsync(includeClosed ?? false, ct).ConfigureAwait(false);
            return Results.Json(overlays, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/fs/listdir", async (string? path, string? targetId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var listing = await kernel.ListHostDirectoryAsync(path, targetId, ct).ConfigureAwait(false);
            return Results.Json(listing, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/targets", (IPergaKernel kernel) =>
        {
            var targets = kernel.ExecutionEnvironments.ListTargets();
            return Results.Json(targets, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/targets", async (HttpContext ctx, IExecutionTargetManager targetManager, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<CreateExecutionTargetRequest>(ctx, ct).ConfigureAwait(false);
            var created = await targetManager.CreateSshTargetAsync(req, ct).ConfigureAwait(false);
            return Results.Json(created, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status201Created);
        });

        v1.MapDelete("/targets/{targetId}", async (string targetId, IExecutionTargetManager targetManager, CancellationToken ct) =>
        {
            var deleted = await targetManager.DeleteTargetAsync(targetId, ct).ConfigureAwait(false);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        v1.MapGet("/targets/{targetId}/providers", async (string targetId, bool? refresh, IPergaKernel kernel, CancellationToken ct) =>
        {
            var providers = await kernel.GetTargetProvidersAsync(targetId, refresh ?? false, ct).ConfigureAwait(false);
            return Results.Json(providers, PergaJsonDefaults.ApiOptions);
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
        v1.MapGet("/areas/{areaId}/workspace-options", async (string areaId, string? cwd, string? templateId, string? targetId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var options = await kernel.GetTrackWorkspaceOptionsAsync(areaId, cwd, templateId, ct, targetId).ConfigureAwait(false);
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

        v1.MapGet("/tracks/{trackId}/views", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var views = await kernel.GetTrackViewsAsync(trackId, ct).ConfigureAwait(false);
            return Results.Json(views, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/tracks/{trackId}/flow", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var flow = await kernel.GetTrackFlowAsync(trackId, ct).ConfigureAwait(false);
            return Results.Json(flow, PergaJsonDefaults.ApiOptions);
        }).Produces<FlowSnapshotDto>();

        v1.MapPost("/tracks/{trackId}/flow/commit", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadOptionalJsonBodyAsync(ctx, () => new FlowCommitRequest(), ct, SessionJsonOptions).ConfigureAwait(false);
            if (body.ExpectedFlowRev is < 0)
                throw KernelException.InvalidArgument("expectedFlowRev must be a non-negative integer.");
            var result = await kernel.CommitTrackFlowAsync(trackId, body.DryRun ?? false, body.ExpectedFlowRev,
                new InteropPrincipal.Human(), ct, body.Content).ConfigureAwait(false);
            return Results.Json(result, PergaJsonDefaults.ApiOptions);
        }).Produces<FlowCommitResultDto>();

        v1.MapPatch("/tracks/{trackId}/data", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var patch = await ReadRequiredJsonBodyAsync<JsonElement>(ctx, ct).ConfigureAwait(false);
            long? expectedDataRev = null;
            if (patch.ValueKind == JsonValueKind.Object &&
                (patch.TryGetProperty("patch", out _) || patch.TryGetProperty("expectedDataRev", out _)))
            {
                if (patch.EnumerateObject().Any(p => p.Name is not ("patch" or "expectedDataRev")))
                    throw KernelException.InvalidArgument("The data envelope accepts only 'patch' and optional 'expectedDataRev'.");
                if (patch.TryGetProperty("expectedDataRev", out var rev))
                {
                    if (rev.ValueKind != JsonValueKind.Number || !rev.TryGetInt64(out var value) || value < 0)
                        throw KernelException.InvalidArgument("expectedDataRev must be a non-negative integer.");
                    expectedDataRev = value;
                }
                if (!patch.TryGetProperty("patch", out var innerPatch) || innerPatch.ValueKind != JsonValueKind.Object)
                    throw KernelException.InvalidArgument("The data envelope requires an object 'patch'.");
                patch = innerPatch.Clone();
            }
            var result = await kernel.PatchTrackDataAsync(trackId, patch, source: "api", callerSessionId: null,
                ct: ct, expectedDataRev: expectedDataRev).ConfigureAwait(false);
            return Results.Json(result, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/tracks/{trackId}/tasks/{taskKey}/cancel", async (string trackId, string taskKey, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadOptionalJsonBodyAsync(ctx, () => new CancelTaskHttpBody(), ct).ConfigureAwait(false);
            var task = await kernel.CancelTaskAsync(trackId, callerSessionId: null, taskKey, body.Reason, ct).ConfigureAwait(false);
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
            var needsKey = request.MessageId is null && (request.Content is not null || request.AttachmentIds is { Count: > 0 });
            request = request with { RequestKey = needsKey ? RequiredMessageKey(ctx) : null };
            return Results.Json(await kernel.SendSessionMessageAsync(sessionId, request, new InteropPrincipal.Human(), ct).ConfigureAwait(false),
                PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/attachments", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            PrepareBinaryAttachmentUpload(ctx, kernel.AttachmentLimits);
            var filename = ReadAttachmentFilenameHeader(ctx);
            var stored = await kernel.UploadSessionAttachmentAsync(sessionId, ctx.Request.Body, filename, ct).ConfigureAwait(false);
            return Results.Json(SessionAttachmentValidation.ToUploadResponse(stored), PergaJsonDefaults.ApiOptions,
                statusCode: StatusCodes.Status201Created);
        });

        v1.MapGet("/sessions/{sessionId}/attachments/{attachmentId}", async (string sessionId, string attachmentId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var attachment = await kernel.GetSessionAttachmentAsync(sessionId, attachmentId, ct).ConfigureAwait(false);
            return await ServeAttachmentAsync(ctx, kernel, attachment, ct).ConfigureAwait(false);
        });

        v1.MapPost("/attachments", async (string? trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            PrepareBinaryAttachmentUpload(ctx, kernel.AttachmentLimits);
            var filename = ReadAttachmentFilenameHeader(ctx);
            var stored = await kernel.UploadStagedAttachmentAsync(trackId, ctx.Request.Body, filename, ct).ConfigureAwait(false);
            return Results.Json(SessionAttachmentValidation.ToUploadResponse(stored), PergaJsonDefaults.ApiOptions,
                statusCode: StatusCodes.Status201Created);
        });

        v1.MapGet("/attachments/{attachmentId}", async (string attachmentId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var attachment = await kernel.GetUnownedStagedAttachmentAsync(attachmentId, ct).ConfigureAwait(false);
            return await ServeAttachmentAsync(ctx, kernel, attachment, ct).ConfigureAwait(false);
        });

        v1.MapDelete("/sessions/{sessionId}/messages/{messageId}", async (string sessionId, string messageId,
            IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.WithdrawSessionMessageAsync(sessionId, messageId, new InteropPrincipal.Human(), ct).ConfigureAwait(false);
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

        v1.Map("/terminals/{termId}/ws", async (string termId, HttpContext ctx, IPergaKernel kernel) =>
        {
            var terminalClient = await kernel.ResolveTerminalClientAsync(termId, ctx.RequestAborted).ConfigureAwait(false);
            await TerminalWebSocketRelayHandler.HandleAsync(ctx, termId, terminalClient).ConfigureAwait(false);
        });

        return endpoints;
    }

    private static void PrepareBinaryAttachmentUpload(HttpContext ctx, AttachmentUploadLimits limits)
    {
        var bodySizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = limits.MaxFileBytes;
        }

        if (ctx.Request.ContentLength is { } contentLength && contentLength > limits.MaxFileBytes)
        {
            throw KernelException.PayloadTooLarge($"Image attachment exceeds maximum allowed size of {limits.MaxFileBytes} bytes.");
        }

        var contentType = ctx.Request.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var mediaType = contentType.Split(';', 2)[0].Trim();
            if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
                mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                throw KernelException.InvalidArgument("Unsupported Content-Type for image upload. Send raw image bytes.");
            }
        }
    }

    private static string? ReadAttachmentFilenameHeader(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Attachment-Filename", out var values) || values.Count == 0)
        {
            return null;
        }

        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return Uri.UnescapeDataString(raw.Trim());
        }
        catch (UriFormatException)
        {
            return raw.Trim();
        }
    }

    private static async Task<IResult> ServeAttachmentAsync(
        HttpContext ctx,
        IPergaKernel kernel,
        StoredSessionAttachment? attachment,
        CancellationToken ct)
    {
        if (attachment is null)
        {
            return HttpErrorHandling.NotFound("Attachment not found.");
        }

        var etag = $"\"{attachment.Sha256}\"";
        var cacheControl = string.Equals(attachment.Status, "bound", StringComparison.Ordinal)
            ? "private, max-age=31536000, immutable"
            : "private, no-store";

        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Content-Security-Policy"] = "sandbox";
        ctx.Response.Headers["Content-Disposition"] = "inline";
        ctx.Response.Headers["Cache-Control"] = cacheControl;
        ctx.Response.Headers["ETag"] = etag;

        if (MatchesIfNoneMatch(ctx.Request.Headers.IfNoneMatch.ToString(), etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var stream = await kernel.OpenAttachmentStreamAsync(attachment, ct).ConfigureAwait(false);
        if (stream is null)
        {
            return HttpErrorHandling.NotFound("Attachment file not found.");
        }

        return Results.File(stream, attachment.ContentType, enableRangeProcessing: true);
    }

    private static bool MatchesIfNoneMatch(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return false;
        }

        foreach (var rawTag in ifNoneMatch.Split(','))
        {
            var tag = rawTag.Trim();
            if (tag == "*")
            {
                return true;
            }

            if (tag.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            {
                tag = tag[2..].Trim();
            }

            if (string.Equals(tag, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
