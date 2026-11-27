using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.Serialization;
using Perga.Host.WebSockets;
using Perga.Kernel.Auth;
using Perga.Kernel.Ports;

namespace Perga.Host.Auth;

internal static partial class AuthEndpoints
{
    private const int AuthBodyLimit = 16_384;
    private static readonly JsonSerializerOptions AuthJson = new(PergaJsonDefaults.ApiOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false
    };

    public static void MapPergaAuth(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth");
        auth.MapPost("/login", LoginAsync).AllowAnonymous()
            .WithMetadata(new JsonRequestContract(typeof(LoginRequest), IsOptional: false), new StrictRequest())
            .Produces<SessionIdentityDto>()
            .Produces<ApiErrorResponse>(400).Produces<ApiErrorResponse>(401).Produces<ApiErrorResponse>(403)
            .Produces<ApiErrorResponse>(413).Produces<ApiErrorResponse>(415).Produces<ApiErrorResponse>(429)
            .WithMetadata(new ResponseHeader(200, WebSessionCookie.SessionIdHeader), new ResponseHeader(200, "Set-Cookie"),
                new ResponseHeader(429, "Retry-After", "integer"));
        auth.MapGet("/session", SessionAsync).RequireAuthorization(PergaAuthPolicies.UserSession)
            .Produces<SessionIdentityDto>().Produces<ApiErrorResponse>(401);
        auth.MapPost("/logout", LogoutAsync).AllowAnonymous().WithMetadata(new RequestOriginGuard.BrowserWriteApi())
            .WithMetadata(new NoRequestBody(), new OptionalWebSession(), new RequestHeader(WebSessionCookie.SessionIdHeader, false))
            .Produces(204);
        MapSetupEndpoints(endpoints, auth);
    }

    private static async Task LoginAsync(HttpContext context, IAuthStore store, IAuthPasswordHasher hasher,
        LoginRateLimiter limiter, PergaHostOptions options, TimeProvider time, ILoggerFactory loggerFactory)
    {
        NoStore(context);
        var result = await LoginCoreAsync(context, store, hasher, limiter, options, time, loggerFactory);
        if (result is not null)
            await result.ExecuteAsync(context);
    }

    private static async Task<IResult?> LoginCoreAsync(HttpContext context, IAuthStore store, IAuthPasswordHasher hasher,
        LoginRateLimiter limiter, PergaHostOptions options, TimeProvider time, ILoggerFactory loggerFactory)
    {
        var ct = context.RequestAborted;
        var (request, error) = await ReadRequestAsync<LoginRequest>(context, "login");
        if (error is not null)
            return error;

        if (string.IsNullOrWhiteSpace(request?.Username) || string.IsNullOrWhiteSpace(request.Password))
            return HttpErrorHandling.BadRequest("invalid_argument", "A username and password are required.");
        if (OwnerCredentials.NormalizeUsername(request.Username) is not { } username ||
            !OwnerCredentials.IsValidPassword(request.Password, 1))
            return InvalidCredentials();

        var password = request.Password;
        var ipKey = RequestIp(context);
        using var attempt = limiter.TryReserve(ipKey, username, out var retrySeconds);
        if (attempt is null)
            return Throttled(context, retrySeconds);

        using var kdf = await limiter.EnterKdfAsync(ct);
        if (kdf is null)
            return Throttled(context, limiter.KdfRetrySeconds);

        var owner = await store.GetOwnerAsync(ct);
        var eligible = owner is { DisabledAtMs: null } && string.Equals(owner.Username, username, StringComparison.OrdinalIgnoreCase);
        ct.ThrowIfCancellationRequested();
        attempt.StartKdf();
        // Finish the work and account for the attempt even if the client disconnects.
        var (verification, upgradedHash) = await Task.Run(() =>
        {
            if (!eligible)
            {
                hasher.VerifyDummy(password);
                return (PasswordVerificationResult.Failed, null);
            }

            var verified = hasher.Verify(password, owner!.PasswordHash);
            return (verified, verified == PasswordVerificationResult.SuccessRehashNeeded ? hasher.Hash(password) : null);
        });
        kdf.Dispose();
        ct.ThrowIfCancellationRequested();

        if (owner is null)
            return HttpErrorHandling.Forbidden("auth_not_configured", "Perga has no owner account.",
                string.IsNullOrWhiteSpace(options.InstallationRoot)
                    ? "Run 'perga auth init-owner --username owner --allow-migrate' on the Perga host."
                    : "Run 'perga-ctl auth init-owner --username owner --allow-migrate' (with --root <install-root> if using a custom root) on the Perga host.");
        if (verification == PasswordVerificationResult.Failed)
            return InvalidCredentials();

        var now = time.GetUtcNow();
        if (upgradedHash is not null)
        {
            try
            {
                await store.UpgradePasswordHashIfVersionMatchesAsync(owner, upgradedHash, now.ToUnixTimeMilliseconds(), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!).LogWarning(ex, "Could not update the owner password hash");
            }
        }

        var (cookie, session) = CreateOwnerSession(context, options, now);
        var identity = await store.TryCreateSessionIfVersionMatchesAsync(owner, session, ct);
        if (identity is null)
            return InvalidCredentials();

        ct.ThrowIfCancellationRequested();
        await WriteSession(context, options, cookie, identity, 200).ExecuteAsync(context);
        ct.ThrowIfCancellationRequested();
        attempt.Succeed();
        return null;
    }

    private static async Task<IResult> SessionAsync(HttpContext context, IAuthStore store, TimeProvider time, ILoggerFactory loggerFactory)
    {
        NoStore(context);
        var session = context.Features.Get<AuthenticatedSession>()!.Identity;
        if (session.Mode == AuthSessionMode.Local)
            return Results.Json(session, PergaJsonDefaults.ApiOptions);

        try
        {
            await store.TouchSessionLastSeenIfStaleAsync(session.SessionId, time.GetUtcNow().ToUnixTimeMilliseconds(), context.RequestAborted);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            loggerFactory.CreateLogger(typeof(AuthEndpoints).FullName!).LogWarning(ex, "Could not update the session last-seen timestamp");
        }
        return Results.Json(session, PergaJsonDefaults.ApiOptions);
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAuthStore store, PergaHostOptions options,
        TimeProvider time, AuthWebSocketRegistry sockets, LocalSessionManager localSessions)
    {
        NoStore(context);
        await context.AuthenticateAsync(PergaAuthSchemes.WebSession);
        var session = context.Features.Get<AuthenticatedSession>();
        if (context.Request.Headers.TryGetValue(WebSessionCookie.SessionIdHeader, out var expected) &&
            (expected.Count != 1 || (session is not null && expected[0] != session.Identity.SessionId)))
            return Results.NoContent();

        if (session is not null)
        {
            if (session.Identity.Mode == AuthSessionMode.Local)
                localSessions.Revoke(session.TokenHash, sockets);
            else
            {
                await store.RevokeSessionByTokenHashAsync(session.TokenHash, time.GetUtcNow().ToUnixTimeMilliseconds(), CancellationToken.None);
                sockets.DisconnectSession(session.Identity.SessionId);
            }
        }
        context.Response.Cookies.Delete(WebSessionCookie.Name(context, options), WebSessionCookie.Attributes(context, options));
        return Results.NoContent();
    }

    private static async Task<(T? Request, IResult? Error)> ReadRequestAsync<T>(HttpContext context, string name) where T : class
    {
        if (!RequestOriginGuard.HasApplicationJson(context.Request))
            return (null, Error(415, "unsupported_media_type", "The request requires application/json."));
        if (context.Request.ContentLength > AuthBodyLimit)
            return (null, BodyTooLarge());

        var buffer = new byte[AuthBodyLimit + 1];
        try
        {
            var length = await context.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, context.RequestAborted);
            return length > AuthBodyLimit
                ? (null, BodyTooLarge())
                : (JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), AuthJson), null);
        }
        catch (JsonException)
        {
            return (null, HttpErrorHandling.BadRequest("invalid_json", $"Invalid {name} request."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static (string Cookie, NewAuthSession Session) CreateOwnerSession(
        HttpContext context, PergaHostOptions options, DateTimeOffset now)
    {
        var (cookie, hash) = WebSessionCookie.Create();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return (cookie, new($"aus_{Guid.NewGuid():N}", hash, now.ToUnixTimeMilliseconds(),
            now.AddDays(options.AuthSessionLifetimeDays).ToUnixTimeMilliseconds(),
            userAgent.Length > 512 ? userAgent[..512] : userAgent, RequestIp(context)));
    }

    private static string RequestIp(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        return (ip?.IsIPv4MappedToIPv6 == true ? ip.MapToIPv4() : ip)?.ToString() ?? "loopback";
    }

    private static void WriteSessionCookie(HttpContext context, PergaHostOptions options, string cookie, SessionIdentityDto identity)
    {
        var attributes = WebSessionCookie.Attributes(context, options);
        attributes.Expires = DateTimeOffset.FromUnixTimeMilliseconds(identity.ExpiresAtMs);
        context.Response.Cookies.Append(WebSessionCookie.Name(context, options), cookie, attributes);
    }

    private static IResult WriteSession(HttpContext context, PergaHostOptions options, string cookie, SessionIdentityDto identity, int status)
    {
        WriteSessionCookie(context, options, cookie, identity);
        context.Response.Headers[WebSessionCookie.SessionIdHeader] = identity.SessionId;
        return Results.Json(identity, PergaJsonDefaults.ApiOptions, statusCode: status);
    }

    private static IResult BodyTooLarge() => Error(413, "request_body_too_large", $"Authentication requests are limited to {AuthBodyLimit} bytes.");
    private static IResult InvalidCredentials() => Error(401, "invalid_credentials", "Invalid username or password.");

    private static IResult Throttled(HttpContext context, int retrySeconds)
    {
        context.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
        return Error(429, "login_throttled", "Too many login attempts. Try again later.");
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ApiErrorResponse(code, message), PergaJsonDefaults.ApiOptions, statusCode: status);

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Vary = "Cookie";
    }

    private sealed record LoginRequest(string? Username, string? Password);
}
