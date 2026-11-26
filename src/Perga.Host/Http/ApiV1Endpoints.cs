using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Perga.Contracts;
using Perga.Host.Auth;
using Perga.Host.Serialization;
using Perga.Host.WebSockets;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.TermClient;

namespace Perga.Host.Http;

public sealed record CancelTaskHttpBody(
    string? Reason = null);

internal sealed record MultipartMessagePayload(string? Content = null, string Mode = "default", string? TurnId = null,
    int? ExpectedEpoch = null);

public static class ApiV1Endpoints
{
    private static readonly JsonSerializerOptions SessionJsonOptions = new(PergaJsonDefaults.ApiOptions)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private static readonly FileExtensionContentTypeProvider RawMimeProvider = CreateMimeProvider();

    public static IEndpointRouteBuilder MapPergaApiV1(this IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapGroup("/api/v1").RequireAuthorization(PergaAuthPolicies.UserSession)
            .WithMetadata(new RequestOriginGuard.BrowserWriteApi());

        v1.MapGet("/read-receipts", async (IPergaKernel kernel, CancellationToken ct) =>
            TypedResults.Json(await kernel.GetReadReceiptsAsync(ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions));
        v1.MapPost("/read-receipts/{kind}/{targetId}/read", async (string kind, string targetId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<MarkReadRequest>(ctx, ct).ConfigureAwait(false);
            return TypedResults.Json(await kernel.MarkReadAsync(kind, targetId, req.ReadThroughMs ?? 0, ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions);
        }).WithMetadata(new JsonRequestContract(typeof(MarkReadRequest), IsOptional: false));
        v1.MapPost("/read-receipts/{kind}/{targetId}/unread", async (string kind, string targetId, IPergaKernel kernel, CancellationToken ct) =>
            TypedResults.Json(await kernel.MarkUnreadAsync(kind, targetId, ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions)).WithMetadata(new NoRequestBody());

        // Empty segments do not bind {targetId}; match only literal //read and //unread.
        v1.MapPost("/read-receipts/{kind}/{**emptyTarget:regex(^/(?:read|unread)$)}", () =>
        {
            throw KernelException.InvalidArgument("TargetId cannot be empty.");
        }).ExcludeFromDescription().WithMetadata(new NoRequestBody());

        // ------------------- System -------------------
        v1.MapGet("/bootstrap", async (bool? refresh, string? card_id, IPergaKernel kernel, CancellationToken ct) =>
        {
            var dto = await kernel.GetBootstrapAsync(refresh ?? false, ct, card_id).ConfigureAwait(false);
            return TypedResults.Json(dto, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/overlays", async (bool? includeClosed, IPergaKernel kernel, CancellationToken ct) =>
        {
            var overlays = await kernel.GetOverlaysAsync(includeClosed ?? false, ct).ConfigureAwait(false);
            return TypedResults.Json(overlays, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/fs/listdir", async (string? path, string? targetId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var listing = await kernel.ListHostDirectoryAsync(path, targetId, ct).ConfigureAwait(false);
            return TypedResults.Json(listing, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/targets", (IPergaKernel kernel) =>
        {
            var targets = kernel.ExecutionEnvironments.ListTargets();
            return TypedResults.Json(targets, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/targets", async (HttpContext ctx, IExecutionTargetManager targetManager, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<CreateExecutionTargetRequest>(ctx, ct).ConfigureAwait(false);
            var created = await targetManager.CreateSshTargetAsync(req, ct).ConfigureAwait(false);
            return TypedResults.Json(created, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(new JsonRequestContract(typeof(CreateExecutionTargetRequest), IsOptional: false)).WithMetadata(new SuccessStatus(201));

        v1.MapDelete("/targets/{targetId}", async (string targetId, IExecutionTargetManager targetManager, CancellationToken ct) =>
        {
            var deleted = await targetManager.DeleteTargetAsync(targetId, ct).ConfigureAwait(false);
            return deleted ? TypedResults.NoContent() : Results.NotFound();
        }).Produces(204).Produces(404);

        v1.MapGet("/targets/{targetId}/providers", async (string targetId, bool? refresh, IPergaKernel kernel, CancellationToken ct) =>
        {
            var providers = await kernel.GetTargetProvidersAsync(targetId, refresh ?? false, ct).ConfigureAwait(false);
            return TypedResults.Json(providers, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/templates", (IPergaKernel kernel) =>
        {
            var templates = kernel.ListTemplates();
            return TypedResults.Json(templates, PergaJsonDefaults.ApiOptions);
        });

        // ------------------- Areas -------------------
        v1.MapGet("/areas", async (IPergaKernel kernel, CancellationToken ct) =>
        {
            var areas = await kernel.ListAreasAsync(ct).ConfigureAwait(false);
            return TypedResults.Json(areas, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/areas", async (HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<CreateAreaRequest>(ctx, ct).ConfigureAwait(false);
            var created = await kernel.CreateAreaAsync(req, ct, HttpIdempotency.OptionalKey(ctx)).ConfigureAwait(false);
            return TypedResults.Json(created, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(new JsonRequestContract(typeof(CreateAreaRequest), IsOptional: false)).WithMetadata(new SuccessStatus(201)).WithMetadata(new RequestHeader("Idempotency-Key", false));

        v1.MapPatch("/areas/{areaId}", async (string areaId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<PatchAreaRequest>(ctx, ct).ConfigureAwait(false);
            var updated = await kernel.PatchAreaAsync(areaId, req, ct).ConfigureAwait(false);
            return TypedResults.Json(updated, PergaJsonDefaults.ApiOptions);
        }).WithMetadata(new JsonRequestContract(typeof(PatchAreaRequest), IsOptional: false));

        v1.MapDelete("/areas/{areaId}", async (string areaId, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.DeleteAreaAsync(areaId, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        // ------------------- Tracks -------------------
        v1.MapGet("/areas/{areaId}/workspace-options", async (string areaId, string? cwd, string? templateId, string? targetId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var options = await kernel.GetTrackWorkspaceOptionsAsync(areaId, cwd, templateId, ct, targetId).ConfigureAwait(false);
            return TypedResults.Json(options, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/areas/{areaId}/tracks", async (string areaId, bool? includeClosed, IPergaKernel kernel, CancellationToken ct) =>
        {
            _ = await kernel.GetAreaAsync(areaId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound($"Area '{areaId}' not found.");
            var tracks = await kernel.ListTracksAsync(areaId, includeClosed ?? false, ct).ConfigureAwait(false);
            return TypedResults.Json(tracks, PergaJsonDefaults.ApiOptions);
        }).Produces<IReadOnlyList<TrackDto>>();

        v1.MapPost("/tracks", async (HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<CreateTrackRequest>(ctx, ct).ConfigureAwait(false);
            var snapshot = await kernel.CreateTrackAsync(req, ct, HttpIdempotency.OptionalKey(ctx)).ConfigureAwait(false);
            return TypedResults.Json(snapshot, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status201Created);
        }).Produces<TrackAggregateSnapshot>(StatusCodes.Status201Created).WithMetadata(new JsonRequestContract(typeof(CreateTrackRequest), IsOptional: false)).WithMetadata(new SuccessStatus(201)).WithMetadata(new RequestHeader("Idempotency-Key", false));

        v1.MapGet("/tracks/{trackId}", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var snapshot = await kernel.GetTrackAggregateAsync(trackId, ct).ConfigureAwait(false);
            return TypedResults.Json(snapshot, PergaJsonDefaults.ApiOptions);
        }).Produces<TrackAggregateSnapshot>();

        v1.MapGet("/tracks/{trackId}/template", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var template = await kernel.Actors.GetOrActivate(trackId).InspectCurrentTemplateAsync(ct).ConfigureAwait(false);
            return TypedResults.Json(template, PergaJsonDefaults.ApiOptions);
        }).Produces<JsonElement>();

        v1.MapDelete("/tracks/{trackId}/template", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var track = await kernel.Actors.GetOrActivate(trackId).ClearTemplateAsync(ct).ConfigureAwait(false);
            return TypedResults.Json(track, PergaJsonDefaults.ApiOptions);
        }).Produces<TrackDto>();

        v1.MapPatch("/tracks/{trackId}/template/inputs", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var patch = await ReadRequiredJsonBodyAsync<JsonElement>(ctx, ct).ConfigureAwait(false);
            var track = await kernel.Actors.GetOrActivate(trackId).UpdateTemplateInputsAsync(patch, ct).ConfigureAwait(false);
            return TypedResults.Json(track, PergaJsonDefaults.ApiOptions);
        }).Produces<TrackDto>().WithMetadata(new JsonRequestContract(typeof(JsonElement), IsOptional: false));

        v1.MapPost("/tracks/{trackId}/workspace/retry", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.RetryWorkspaceAsync(trackId, ct).ConfigureAwait(false);
            return TypedResults.Accepted((string?)null);
        }).WithMetadata(new NoRequestBody());

        v1.MapPatch("/tracks/{trackId}", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<PatchTrackRequest>(ctx, ct).ConfigureAwait(false);
            var updated = await kernel.PatchTrackAsync(trackId, req, ct).ConfigureAwait(false);
            return TypedResults.Json(updated, PergaJsonDefaults.ApiOptions);
        }).Produces<TrackDto>().WithMetadata(new JsonRequestContract(typeof(PatchTrackRequest), IsOptional: false));

        v1.MapDelete("/tracks/{trackId}", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.DeleteTrackAsync(trackId, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        // ------------------- Workspace & VCS -------------------
        v1.MapGet("/tracks/{trackId}/files", async (string trackId, string? path, int? maxBytes, IPergaKernel kernel, CancellationToken ct) =>
        {
            var result = await kernel.BrowseOrReadWorkspaceFileAsync(
                trackId,
                path,
                maxBytes ?? (256 * 1024),
                ct).ConfigureAwait(false);
            return TypedResults.Json(result, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/tracks/{trackId}/files/search", async (string trackId, string? q, int? limit, IPergaKernel kernel, CancellationToken ct) =>
        {
            var result = await kernel.SearchWorkspaceFilesAsync(trackId, q, limit ?? WorkspaceFileSearchResultDto.DefaultLimit, ct)
                .ConfigureAwait(false);
            return TypedResults.Json(result, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/tracks/{trackId}/files/raw", async (string trackId, string? path, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var (resolvedPath, stream) = await kernel.OpenWorkspaceRawFileAsync(trackId, path, ct).ConfigureAwait(false);
            if (!RawMimeProvider.TryGetContentType(resolvedPath, out var contentType) || string.IsNullOrWhiteSpace(contentType))
            {
                contentType = "application/octet-stream";
            }

            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (RequiresSandboxCsp(resolvedPath, contentType))
            {
                ctx.Response.Headers["Content-Security-Policy"] = "sandbox";
            }

            return TypedResults.File(stream, contentType, enableRangeProcessing: true);
        });

        v1.MapGet("/tracks/{trackId}/vcs-diff", async (string trackId, string? path, string? fromRev, string? toRev, IPergaKernel kernel, CancellationToken ct) =>
        {
            var diff = await kernel.GetTrackVcsDiffAsync(trackId, fromRev, toRev, path, ct).ConfigureAwait(false);
            return TypedResults.Json(diff, PergaJsonDefaults.ApiOptions);
        });

        // ------------------- Report & Tasks -------------------
        v1.MapGet("/tracks/{trackId}/report", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var report = await kernel.ReadReportAsync(trackId, callerSessionId: null, ct).ConfigureAwait(false);
            return TypedResults.Json(report, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/tracks/{trackId}/views", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var views = await kernel.GetTrackViewsAsync(trackId, ct).ConfigureAwait(false);
            return TypedResults.Json(views, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/tracks/{trackId}/flow", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var flow = await kernel.GetTrackFlowAsync(trackId, ct).ConfigureAwait(false);
            return TypedResults.Json(flow, PergaJsonDefaults.ApiOptions);
        }).Produces<FlowSnapshotDto>();

        v1.MapPost("/tracks/{trackId}/flow/commit", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadOptionalJsonBodyAsync(ctx, () => new FlowCommitRequest(), ct, SessionJsonOptions).ConfigureAwait(false);
            if (body.ExpectedFlowRev is < 0)
                throw KernelException.InvalidArgument("expectedFlowRev must be a non-negative integer.");
            var result = await kernel.CommitTrackFlowAsync(trackId, body.DryRun ?? false, body.ExpectedFlowRev,
                AuthenticatedSession.Human(ctx), ct, body.Content).ConfigureAwait(false);
            return TypedResults.Json(result, PergaJsonDefaults.ApiOptions);
        }).Produces<FlowCommitResultDto>().WithMetadata(new JsonRequestContract(typeof(FlowCommitRequest), IsOptional: true)).WithMetadata(new StrictRequest());

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
            return TypedResults.Json(result, PergaJsonDefaults.ApiOptions);
        }).WithMetadata(new JsonRequestContract(typeof(JsonElement), IsOptional: false));

        v1.MapPost("/tracks/{trackId}/tasks/{taskKey}/cancel", async (string trackId, string taskKey, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadOptionalJsonBodyAsync(ctx, () => new CancelTaskHttpBody(), ct).ConfigureAwait(false);
            var task = await kernel.CancelTaskAsync(trackId, callerSessionId: null, taskKey, body.Reason, ct).ConfigureAwait(false);
            return TypedResults.Json(task, PergaJsonDefaults.ApiOptions);
        }).WithMetadata(new JsonRequestContract(typeof(CancelTaskHttpBody), IsOptional: true));

        v1.MapGet("/tracks/{trackId}/media/{fileName}", async (string trackId, string fileName, HttpContext ctx,
            ITrackReadStore store, CancellationToken ct) =>
        {
            var media = await store.OpenTrackMediaAsync(trackId, fileName, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound("Image attachment not found.");
            ctx.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers.ContentSecurityPolicy = "sandbox";
            return TypedResults.File(media.Content, media.ContentType);
        }).WithMetadata(new BinaryResponse(ImageAttachmentContract.MediaTypes),
            new ResponseHeader(200, "Cache-Control"), new ResponseHeader(200, "X-Content-Type-Options"),
            new ResponseHeader(200, "Content-Security-Policy")).Produces<ApiErrorResponse>(404);

        // ------------------- Unified sessions -------------------
        v1.MapGet("/tracks/{trackId}/sessions", async (string trackId, bool? includeArchived, string? parentSessionId,
            HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
            TypedResults.Json(await kernel.ListSessionsAsync(trackId, new(includeArchived ?? false, parentSessionId),
                AuthenticatedSession.Human(ctx), ct).ConfigureAwait(false), PergaJsonDefaults.ApiOptions));

        v1.MapPost("/tracks/{trackId}/sessions", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var key = RequiredMessageKey(ctx);
            CreateSessionRequest request;
            if (MultipartImageInput.IsMultipart(ctx.Request))
            {
                var (payload, images) = await MultipartImageInput.ReadAsync<CreateSessionRequest>(ctx, SessionJsonOptions, ct).ConfigureAwait(false);
                request = payload with { Images = images };
            }
            else
                request = await ReadOptionalJsonBodyAsync(ctx, () => new CreateSessionRequest(), ct, SessionJsonOptions).ConfigureAwait(false);
            var (receipt, replay) = await kernel.CreateSessionAsync(trackId, request, key, AuthenticatedSession.Human(ctx), ct).ConfigureAwait(false);
            return TypedResults.Json(receipt, PergaJsonDefaults.ApiOptions,
                statusCode: replay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        }).WithMetadata(new JsonRequestContract(typeof(CreateSessionRequest), IsOptional: true),
            new MultipartImageRequest(typeof(CreateSessionRequest), "prompt may be omitted or empty for image-only input.")).WithMetadata(new SuccessStatus(201, ReplayedAsOk: true)).WithMetadata(new StrictRequest()).WithMetadata(new RequestHeader("Idempotency-Key", true));

        v1.MapGet("/sessions/{sessionId}/items", async (string sessionId, long? afterId, long? beforeId, int? limit,
            bool? latest, string? activityTurnId, long? afterSequence, int? waitMs, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            if (limit is < 1 or > 100 || waitMs is < 0 or > 10000 || afterId is < 0 || beforeId is < 0 || afterSequence is < 0)
                throw KernelException.InvalidArgument("Invalid session read range.");
            var page = await kernel.GetSessionItemsAsync(sessionId,
                new(afterId ?? 0, beforeId, limit ?? 20, latest ?? false, activityTurnId, afterSequence, waitMs ?? 0),
                AuthenticatedSession.Human(ctx), ct).ConfigureAwait(false);
            var items = page.Items.Select(item =>
            {
                if (item.OwnerRequestKey is not { } requestKey || item.Payload.ValueKind != JsonValueKind.Object)
                    return item;
                var payload = item.Payload.Deserialize<Dictionary<string, JsonElement>>()!;
                payload["requestKey"] = JsonSerializer.SerializeToElement(requestKey);
                return item with { Payload = JsonSerializer.SerializeToElement(payload) };
            }).ToArray();
            return TypedResults.Json(page with { Items = items }, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPatch("/sessions/{sessionId}", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var request = await ReadRequiredJsonBodyAsync<UpdateSessionRequest>(ctx, ct, SessionJsonOptions).ConfigureAwait(false);
            return TypedResults.Json(await kernel.UpdateSessionAsync(sessionId, request, AuthenticatedSession.Human(ctx), ct).ConfigureAwait(false),
                PergaJsonDefaults.ApiOptions);
        }).WithMetadata(new JsonRequestContract(typeof(UpdateSessionRequest), IsOptional: false)).WithMetadata(new StrictRequest());

        v1.MapPost("/sessions/{sessionId}/messages", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            SendSessionMessageRequest request;
            if (MultipartImageInput.IsMultipart(ctx.Request))
            {
                var (payload, images) = await MultipartImageInput.ReadAsync<MultipartMessagePayload>(ctx, SessionJsonOptions, ct).ConfigureAwait(false);
                request = new(payload.Content, RequiredMessageKey(ctx), payload.Mode, payload.TurnId, ExpectedEpoch: payload.ExpectedEpoch)
                { Images = images };
            }
            else
            {
                request = await ReadRequiredJsonBodyAsync<SendSessionMessageRequest>(ctx, ct, SessionJsonOptions).ConfigureAwait(false);
                request = request with { RequestKey = request.Content is not null ? RequiredMessageKey(ctx) : null };
            }
            return TypedResults.Json(await kernel.SendSessionMessageAsync(sessionId, request, AuthenticatedSession.Human(ctx), ct).ConfigureAwait(false),
                PergaJsonDefaults.ApiOptions);
        }).WithMetadata(new JsonRequestContract(typeof(SendSessionMessageRequest), IsOptional: false),
            new MultipartImageRequest(typeof(MultipartMessagePayload), "content may be omitted or empty for image-only input."))
            .WithMetadata(new StrictRequest(), new RequestHeader("Idempotency-Key", false,
                "Required for multipart requests and JSON requests containing content."));

        v1.MapDelete("/sessions/{sessionId}/messages/{messageId}", async (string sessionId, string messageId,
            HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.WithdrawSessionMessageAsync(sessionId, messageId, AuthenticatedSession.Human(ctx), ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        v1.MapPost("/sessions/{sessionId}/control", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var request = await ReadRequiredJsonBodyAsync<SessionControlRequest>(ctx, ct, SessionJsonOptions).ConfigureAwait(false);
            return TypedResults.Json(await kernel.ControlSessionAsync(sessionId, request, AuthenticatedSession.Human(ctx), ct).ConfigureAwait(false),
                PergaJsonDefaults.ApiOptions);
        }).WithMetadata(new JsonRequestContract(typeof(SessionControlRequest), IsOptional: false)).WithMetadata(new StrictRequest());

        v1.MapPost("/sessions/{sessionId}/interactions/{interactionId}/respond", async (string sessionId, string interactionId,
            HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var request = await ReadRequiredJsonBodyAsync<RespondInteractionRequest>(ctx, ct, SessionJsonOptions).ConfigureAwait(false);
            return TypedResults.Json(await kernel.RespondInteractionAsync(sessionId, interactionId, request, AuthenticatedSession.Human(ctx), ct).ConfigureAwait(false),
                PergaJsonDefaults.ApiOptions);
        }).WithMetadata(new JsonRequestContract(typeof(RespondInteractionRequest), IsOptional: false)).WithMetadata(new StrictRequest());

        // ------------------- Terminals -------------------
        v1.MapGet("/tracks/{trackId}/terminals", async (string trackId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var terminals = await kernel.ListTerminalsAsync(trackId, ct).ConfigureAwait(false);
            return TypedResults.Json(terminals, PergaJsonDefaults.ApiOptions);
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
            return TypedResults.Json(created, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status201Created);
        }).WithMetadata(new JsonRequestContract(typeof(OpenTerminalRequest), IsOptional: true)).WithMetadata(new SuccessStatus(201)).WithMetadata(new RequestHeader("Idempotency-Key", false));

        v1.MapDelete("/tracks/{trackId}/terminals/{termId}", async (string trackId, string termId, IPergaKernel kernel, CancellationToken ct) =>
        {
            _ = await kernel.CloseTerminalAsync(trackId, termId, graceMs: 2000, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
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
        var key = HttpIdempotency.OptionalKey(ctx);
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
