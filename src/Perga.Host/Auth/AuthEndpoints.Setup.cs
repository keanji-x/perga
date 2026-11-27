using Microsoft.AspNetCore.Authentication;
using Perga.Contracts;
using Perga.Host.Cli;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.Serialization;
using Perga.Host.WebSockets;
using Perga.Kernel.Auth;
using Perga.Kernel.Ports;

namespace Perga.Host.Auth;

internal static partial class AuthEndpoints
{
    private const string LocalLaunchHint = "Open the Perga launch link from your terminal or paste the local launch token.";
    private const string NonLoopbackHint = "Open Perga directly from the host at http://127.0.0.1 or run '" +
        HostStartupOnboarding.InitOwnerCommand + "' on the host.";
    private const string ExistingDataHint = "Run '" + HostStartupOnboarding.InitOwnerCommand + "' on the Perga host.";

    private static void MapSetupEndpoints(IEndpointRouteBuilder endpoints, RouteGroupBuilder auth)
    {
        endpoints.MapGet("/auth/local", LaunchLocalAsync).AllowAnonymous().ExcludeFromDescription();
        auth.MapGet("/setup", SetupStatusAsync).AllowAnonymous().WithMetadata(new OptionalWebSession())
            .Produces<AuthSetupStatusDto>();
        auth.MapPost("/setup", SetupOwnerAsync).AllowAnonymous()
            .WithMetadata(new RequestOriginGuard.BrowserWriteApi(), new StrictRequest(), new OptionalWebSession(),
                new JsonRequestContract(typeof(SetupOwnerRequest), IsOptional: false))
            .Produces<SessionIdentityDto>(201)
            .Produces<ApiErrorResponse>(400).Produces<ApiErrorResponse>(403).Produces<ApiErrorResponse>(409)
            .Produces<ApiErrorResponse>(413).Produces<ApiErrorResponse>(415).Produces<ApiErrorResponse>(429)
            .WithMetadata(new ResponseHeader(201, WebSessionCookie.SessionIdHeader), new ResponseHeader(201, "Set-Cookie"),
                new ResponseHeader(429, "Retry-After", "integer"));
        auth.MapPost("/local", ExchangeLocalTokenAsync).AllowAnonymous()
            .WithMetadata(new RequestOriginGuard.BrowserWriteApi(), new StrictRequest(),
                new JsonRequestContract(typeof(LocalTokenExchangeRequest), IsOptional: false))
            .Produces<SessionIdentityDto>()
            .Produces<ApiErrorResponse>(400).Produces<ApiErrorResponse>(403).Produces<ApiErrorResponse>(409)
            .Produces<ApiErrorResponse>(413).Produces<ApiErrorResponse>(415)
            .WithMetadata(new ResponseHeader(200, WebSessionCookie.SessionIdHeader), new ResponseHeader(200, "Set-Cookie"));
    }

    private static async Task<IResult> SetupStatusAsync(HttpContext context, IAuthStore store, PergaHostOptions options)
    {
        NoStore(context);
        var ct = context.RequestAborted;
        if (await store.GetOwnerAsync(ct) is not null)
            return SetupStatus(new(AuthSetupMode.Configured, false, false));

        if (!LocalSessionManager.IsDirectLoopback(context, options))
            return SetupStatus(new(AuthSetupMode.SetupRequired, true, false, AuthSetupBlockedReason.NonLoopback, NonLoopbackHint));

        if (!options.RequireAuth)
        {
            var local = await GetLocalSessionAsync(context);
            return SetupStatus(local is not null
                ? new(AuthSetupMode.SetupRequired, true, true)
                : new(AuthSetupMode.LocalTokenRequired, true, false, NextStep: LocalLaunchHint));
        }

        return SetupStatus(await store.GetSetupStateAsync(ct) switch
        {
            AuthSetupState.Configured => new(AuthSetupMode.Configured, false, false),
            AuthSetupState.ExistingDataRequiresCli =>
                new(AuthSetupMode.SetupRequired, true, false, AuthSetupBlockedReason.ExistingData, ExistingDataHint),
            _ => new(AuthSetupMode.SetupRequired, true, true)
        });
    }

    private static async Task SetupOwnerAsync(HttpContext context, IAuthStore store, IAuthPasswordHasher hasher,
        LoginRateLimiter limiter, PergaHostOptions options, TimeProvider time, LocalSessionManager localSessions,
        AuthWebSocketRegistry sockets)
    {
        NoStore(context);
        var result = await SetupOwnerCoreAsync(context, store, hasher, limiter, options, time, localSessions, sockets);
        if (result is not null)
            await result.ExecuteAsync(context);
    }

    private static async Task<IResult?> SetupOwnerCoreAsync(HttpContext context, IAuthStore store, IAuthPasswordHasher hasher,
        LoginRateLimiter limiter, PergaHostOptions options, TimeProvider time, LocalSessionManager localSessions,
        AuthWebSocketRegistry sockets)
    {
        if (LocalSourceForbidden(context, options, setup: true) is { } forbidden)
            return forbidden;
        var (request, error) = await ReadRequestAsync<SetupOwnerRequest>(context, "owner setup");
        if (error is not null)
            return error;

        var ct = context.RequestAborted;
        var hasValidLocalSession = await GetLocalSessionAsync(context) is not null;
        if (hasValidLocalSession || !options.RequireAuth)
        {
            if (await store.GetOwnerAsync(ct) is not null)
                return OwnerAlreadyConfigured();
            if (!hasValidLocalSession)
                return HttpErrorHandling.Forbidden("local_session_required", "Open a local session to set up an account.", LocalLaunchHint);
        }
        else
        {
            var state = await store.GetSetupStateAsync(ct);
            if (state == AuthSetupState.Configured)
                return OwnerAlreadyConfigured();
            if (state == AuthSetupState.ExistingDataRequiresCli)
                return SetupRequiresCli();
        }

        if (OwnerCredentials.NormalizeUsername(request?.Username) is not { } username)
            return HttpErrorHandling.BadRequest("invalid_argument", OwnerCredentials.UsernameError);
        if (!OwnerCredentials.TryNormalizeDisplayName(request?.DisplayName, out var displayName))
            return HttpErrorHandling.BadRequest("invalid_argument", OwnerCredentials.DisplayNameError);
        if (!OwnerCredentials.IsValidPassword(request?.Password))
            return HttpErrorHandling.BadRequest("invalid_argument", OwnerCredentials.PasswordError);

        string passwordHash;
        using (var kdf = await limiter.EnterKdfAsync(ct))
        {
            if (kdf is null)
                return Throttled(context, limiter.KdfRetrySeconds);
            ct.ThrowIfCancellationRequested();
            passwordHash = await Task.Run(() => hasher.Hash(request!.Password!));
        }

        var (cookie, session) = CreateOwnerSession(context, options, time.GetUtcNow());
        if (hasValidLocalSession)
            await localSessions.BeginOwnerSetupAsync(ct);
        try
        {
            var result = await store.TryCreateInitialOwnerWithSessionAsync(
                new(username, displayName ?? username, passwordHash), session, allowExistingBusinessData: hasValidLocalSession, ct);
            if (result.Outcome == InitialOwnerSetupOutcome.AlreadyConfigured)
                return OwnerAlreadyConfigured();
            if (result.Outcome == InitialOwnerSetupOutcome.ExistingDataRequiresCli)
                return SetupRequiresCli();

            try
            {
                using var flushDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                flushDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                await WriteSession(context, options, cookie, result.Identity!, 201).ExecuteAsync(context)
                    .WaitAsync(flushDeadline.Token);
                await context.Response.CompleteAsync().WaitAsync(flushDeadline.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The owner is committed. A stalled response cannot delay revocation any longer.
            }
            finally
            {
                localSessions.DisableAndRevokeAll(sockets);
            }
            return null;
        }
        finally
        {
            if (hasValidLocalSession)
                localSessions.EndOwnerSetup();
        }
    }

    private static async Task<IResult> LaunchLocalAsync(HttpContext context, IAuthStore store, PergaHostOptions options,
        TimeProvider time, LocalSessionManager localSessions, AuthWebSocketRegistry sockets)
    {
        NoStore(context);
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (LocalSourceForbidden(context, options) is { } forbidden)
            return forbidden;

        if (context.Request.Query.TryGetValue("token", out var tokens) && tokens.Count == 1 &&
            localSessions.IsActive && await GetLocalSessionAsync(context) is null &&
            !await localSessions.CheckOwnerAsync(store, sockets, context.RequestAborted) &&
            localSessions.TryExchangeLaunchToken(tokens[0], time.GetUtcNow(), sockets, out var cookie, out var identity))
            WriteSessionCookie(context, options, cookie, identity);

        return Results.Redirect("/");
    }

    private static async Task<IResult> ExchangeLocalTokenAsync(HttpContext context, IAuthStore store, PergaHostOptions options,
        TimeProvider time, LocalSessionManager localSessions, AuthWebSocketRegistry sockets)
    {
        NoStore(context);
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (LocalSourceForbidden(context, options) is { } forbidden)
            return forbidden;
        var (request, error) = await ReadRequestAsync<LocalTokenExchangeRequest>(context, "local token");
        if (error is not null)
            return error;

        if (await localSessions.CheckOwnerAsync(store, sockets, context.RequestAborted))
            return OwnerAlreadyConfigured();
        if (!localSessions.TryExchangeLaunchToken(request?.Token, time.GetUtcNow(), sockets, out var cookie, out var identity))
            return Error(403, "invalid_local_token", "The local launch token could not be exchanged.");

        return WriteSession(context, options, cookie, identity, 200);
    }

    private static IResult? LocalSourceForbidden(HttpContext context, PergaHostOptions options, bool setup = false)
    {
        if (!LocalSessionManager.IsDirectLoopback(context, options))
            return setup
                ? HttpErrorHandling.Forbidden("owner_setup_loopback_required",
                    "Browser account setup requires a direct local connection.", NonLoopbackHint)
                : HttpErrorHandling.Forbidden("local_token_loopback_required", "A local launch token requires a direct local connection.");
        var request = context.Request;
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var sites) &&
            (sites.Count != 1 || sites[0] is not ("same-origin" or "same-site" or "none")))
            return HttpErrorHandling.Forbidden("request_origin_forbidden", "Request browser origin is not allowed.");
        if (request.Headers.TryGetValue("Origin", out var origins) &&
            (origins.Count != 1 || !RequestOriginGuard.TryOrigin(origins[0], out var origin) ||
             !RequestOriginGuard.TryOrigin($"{request.Scheme}://{request.Host}", out var ownOrigin) || origin != ownOrigin))
            return HttpErrorHandling.Forbidden("request_origin_forbidden", "Request browser origin is not allowed.");
        return null;
    }

    private static async Task<AuthenticatedSession?> GetLocalSessionAsync(HttpContext context)
    {
        await context.AuthenticateAsync(PergaAuthSchemes.WebSession);
        return context.Features.Get<AuthenticatedSession>() is { Identity.Mode: AuthSessionMode.Local } session ? session : null;
    }

    private static IResult SetupStatus(AuthSetupStatusDto status) => Results.Json(status, PergaJsonDefaults.ApiOptions);
    private static IResult OwnerAlreadyConfigured() => Error(409, "owner_already_configured", "An owner account already exists.");
    private static IResult SetupRequiresCli() => HttpErrorHandling.Forbidden("owner_setup_requires_cli",
        "Existing data requires owner setup on the Perga host.", ExistingDataHint);

    private sealed record SetupOwnerRequest(string? Username, string? Password, string? DisplayName);
    private sealed record LocalTokenExchangeRequest(string? Token);
}
