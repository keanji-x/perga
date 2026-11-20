using System.Text.Json;
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

public sealed record SendMessageHttpBody(
    string Content,
    bool? SteerIfRunning = null,
    bool? QueueIfBusy = null,
    string? AttemptId = null,
    string? TurnId = null,
    bool AllowQueue = false,
    WorkerMessageMode? Mode = null);

public sealed record InterruptTurnHttpBody(string? AttemptId, string TurnId);

public sealed record ResumeProviderSessionHttpBody(string TurnId);

public sealed record SteerQueuedMessageHttpBody(string TurnId);

public sealed record SteerMessageHttpBody(string TurnId, string Content);

public sealed record CancelTaskHttpBody(
    string? Reason = null);

public sealed record AcceptTaskHttpBody(
    string? Note = null);

public sealed record RejectTaskHttpBody(
    string? Note = null,
    bool? ReQueue = null);

public static class ApiV1Endpoints
{
    private static readonly FileExtensionContentTypeProvider RawMimeProvider = CreateMimeProvider();

    public static IEndpointRouteBuilder MapPergaApiV1(this IEndpointRouteBuilder endpoints)
    {
        var v1 = endpoints.MapGroup("/api/v1");

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

        v1.MapPost("/tracks/{trackId}/tasks/dispatch", async (string trackId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var keys = ctx.Request.Headers["Idempotency-Key"];
            if (keys.Count > 1)
                return HttpErrorHandling.BadRequest("invalid_argument", "Exactly one Idempotency-Key is allowed.");
            var request = await ReadRequiredJsonBodyAsync<DispatchWorkerRequest>(ctx, ct).ConfigureAwait(false);
            var receipt = await kernel.DispatchWorkerAsync(trackId, request, keys.Count == 0 ? null : keys.ToString().Trim(), caller: null, ct)
                .ConfigureAwait(false);
            return Results.Json(receipt, PergaJsonDefaults.ApiOptions, statusCode: StatusCodes.Status202Accepted);
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
            {
                return HttpErrorHandling.BadRequest("missing_idempotency_key", "Idempotency-Key HTTP header is required.");
            }

            if (key.Count != 1)
            {
                return HttpErrorHandling.BadRequest("invalid_argument", "Exactly one Idempotency-Key is allowed.");
            }

            var req = await ReadOptionalJsonBodyAsync(ctx, () => new CreateSessionRequest(), ct).ConfigureAwait(false);
            var (receipt, replay) = await kernel.CreateSessionWithReceiptAsync(trackId, req, key.ToString().Trim(), ct)
                .ConfigureAwait(false);
            return Results.Json(
                receipt,
                PergaJsonDefaults.ApiOptions,
                statusCode: replay ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        });

        v1.MapPatch("/sessions/{sessionId}", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadPatchSessionRequestAsync(ctx, ct).ConfigureAwait(false);
            var patched = await kernel.PatchSessionAsync(sessionId, req, ct).ConfigureAwait(false);
            return Results.Json(patched, PergaJsonDefaults.ApiOptions);
        });

        v1.MapGet("/sessions/{sessionId}/items", async (string sessionId, long? afterId, int? limit, bool? latest,
            long? beforeId, string? activityTurnId, long? afterSequence, int? waitMs, IPergaKernel kernel, CancellationToken ct) =>
        {
            var page = await kernel.GetSessionItemsAsync(
                sessionId,
                afterId ?? 0,
                limit ?? 100,
                ct, latest ?? false, beforeId, activityTurnId: activityTurnId, afterSequence: afterSequence, waitMs: waitMs ?? 0).ConfigureAwait(false);
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
            {
                return HttpErrorHandling.BadRequest("invalid_argument", "Exactly one Idempotency-Key is allowed.");
            }

            var idempotencyKey = idemValues.ToString().Trim();
            var body = await ReadRequiredJsonBodyAsync<SendMessageHttpBody>(ctx, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body.Content))
            {
                return HttpErrorHandling.BadRequest("invalid_argument", "Message content cannot be empty.");
            }

            if (body.AttemptId is not null)
            {
                var result = await kernel.SendWorkerMessageAsync(sessionId,
                    new WorkerMessageRequest(body.AttemptId, body.Content, idempotencyKey, body.TurnId, body.AllowQueue, body.Mode),
                    caller: null, ct).ConfigureAwait(false);
                return Results.Json(result, PergaJsonDefaults.ApiOptions,
                    statusCode: result.Delivery == "queued" ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
            }

            if (body.Mode is not null)
                return HttpErrorHandling.BadRequest("invalid_argument", "Worker delivery mode requires attemptId.");

            if (body.SteerIfRunning == true && body.QueueIfBusy == true)
                return HttpErrorHandling.BadRequest("invalid_argument", "Choose either queuing or steering.");

            var (receipt, steered, replay, queued) = await kernel.AcceptMessageAsync(
                sessionId, new SendMessageRequest(body.Content, idempotencyKey, body.SteerIfRunning ?? false, body.QueueIfBusy ?? false), ct)
                .ConfigureAwait(false);
            if (queued is not null)
                return Results.Json(queued, PergaJsonDefaults.ApiOptions,
                    statusCode: replay ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
            if (receipt is null)
            {
                return Results.Json(steered, PergaJsonDefaults.ApiOptions);
            }

            ctx.Response.Headers.Location = receipt.StatusUrl;
            return Results.Json(
                receipt,
                PergaJsonDefaults.ApiOptions,
                statusCode: replay ? StatusCodes.Status200OK : StatusCodes.Status202Accepted);
        });

        v1.MapPost("/sessions/{sessionId}/messages/steer", async (
            string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadRequiredJsonBodyAsync<SteerMessageHttpBody>(ctx, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body.Content) || string.IsNullOrWhiteSpace(body.TurnId))
                return HttpErrorHandling.BadRequest("invalid_argument", "A target turn and message are required.");

            var keys = ctx.Request.Headers["Idempotency-Key"];
            if (keys.Count > 1)
                return HttpErrorHandling.BadRequest("invalid_argument", "Only one Idempotency-Key is allowed.");
            var result = await kernel.SteerMessageAsync(sessionId, body.TurnId, body.Content, ct,
                keys.Count == 0 ? null : keys.ToString().Trim()).ConfigureAwait(false);
            return Results.Json(result, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/messages/queue/{inputId}/steer", async (
            string sessionId, string inputId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadRequiredJsonBodyAsync<SteerQueuedMessageHttpBody>(ctx, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body.TurnId))
                return HttpErrorHandling.BadRequest("invalid_argument", "A target turn is required.");
            var result = await kernel.SteerQueuedMessageAsync(sessionId, inputId, body.TurnId, ct).ConfigureAwait(false);
            return Results.Json(result, PergaJsonDefaults.ApiOptions);
        });

        v1.MapDelete("/sessions/{sessionId}/messages/queue/{inputId}", async (
            string sessionId, string inputId, IPergaKernel kernel, CancellationToken ct) =>
        {
            await kernel.RemoveQueuedMessageAsync(sessionId, inputId, ct).ConfigureAwait(false);
            return Results.NoContent();
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

        v1.MapPut("/sessions/{sessionId}/model", async (
            string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var req = await ReadRequiredJsonBodyAsync<UpdateSessionModelRequest>(ctx, ct).ConfigureAwait(false);
            var session = await kernel.UpdateSessionModelAsync(sessionId, req, ct).ConfigureAwait(false);
            return Results.Json(session, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/resume", async (
            string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadRequiredJsonBodyAsync<ResumeProviderSessionHttpBody>(ctx, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body.TurnId))
                return HttpErrorHandling.BadRequest("invalid_argument", "A target turn is required.");
            var session = await kernel.ResumeProviderSessionAsync(sessionId, body.TurnId, ct).ConfigureAwait(false);
            return Results.Json(session, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/interrupt", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var target = await ReadOptionalJsonBodyAsync<InterruptTurnHttpBody?>(ctx, () => null, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(target?.TurnId))
                return HttpErrorHandling.BadRequest("invalid_argument", "A target turn is required.");
            if (target.AttemptId is null)
                await kernel.InterruptSessionAsync(sessionId, ct, target.TurnId).ConfigureAwait(false);
            else
                await kernel.InterruptWorkerAsync(sessionId, target.AttemptId, target.TurnId, caller: null, ct).ConfigureAwait(false);
            return Results.Json(new { ok = true }, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPost("/sessions/{sessionId}/compact", async (string sessionId, IPergaKernel kernel, CancellationToken ct) =>
        {
            var compacted = await kernel.CompactSessionAsync(sessionId, ct).ConfigureAwait(false);
            return Results.Json(compacted, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPatch("/sessions/{sessionId}/settings", async (string sessionId, HttpContext ctx, IPergaKernel kernel,
            WorkerApprovalAuthorization approval, CancellationToken ct) =>
        {
            var request = await ReadRequiredJsonBodyAsync<UpdateWorkerSettingsRequest>(ctx, ct).ConfigureAwait(false);
            var settings = await kernel.UpdateWorkerSettingsAsync(sessionId, request, caller: null, ct,
                verifiedApprovalOwner: approval.Verify(ctx)).ConfigureAwait(false);
            return Results.Json(settings, PergaJsonDefaults.ApiOptions);
        });

        v1.MapPut("/sessions/{sessionId}/permission-mode", async (string sessionId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadRequiredJsonBodyAsync<SetPermissionModeHttpBody>(ctx, ct).ConfigureAwait(false);
            var mode = body.PermissionMode;
            if (string.IsNullOrWhiteSpace(mode))
            {
                return HttpErrorHandling.BadRequest("invalid_argument", "permissionMode is required.");
            }

            var updated = await kernel.SetSessionPermissionModeAsync(sessionId, mode, ct).ConfigureAwait(false);
            return Results.Json(updated, PergaJsonDefaults.ApiOptions);
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

        v1.MapPost("/tracks/{trackId}/executions/{executionId}/approvals/{approvalId}/decision", async (
            string trackId, string executionId, string approvalId, HttpContext ctx, IPergaKernel kernel, CancellationToken ct) =>
        {
            var body = await ReadRequiredJsonBodyAsync<ApprovalDecisionRequest>(ctx, ct).ConfigureAwait(false);
            var approval = await kernel.DecideApprovalAsync(trackId, executionId, approvalId, body.OptionId, ct).ConfigureAwait(false);
            return Results.Json(approval, PergaJsonDefaults.ApiOptions);
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

    private static async Task<PatchSessionRequest> ReadPatchSessionRequestAsync(HttpContext ctx, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw KernelException.InvalidArgument("PATCH /sessions/{sessionId} body must be a JSON object.");
        }

        var parsed = root.Deserialize<PatchSessionRequest>(PergaJsonDefaults.ApiOptions) ?? new PatchSessionRequest();
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "model", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.Null ||
                    (prop.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(prop.Value.GetString())))
                {
                    parsed = parsed with { Model = null, ClearModel = true };
                }

                break;
            }
        }

        return parsed;
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

    private sealed record SetPermissionModeHttpBody(
        string? PermissionMode);
}
