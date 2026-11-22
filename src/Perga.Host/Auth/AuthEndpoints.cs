using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.Serialization;
using Perga.Host.WebSockets;
using Perga.Kernel.Ports;

namespace Perga.Host.Auth;

internal static class AuthEndpoints
{
    private const int LoginBodyLimit = 4096;
    private static readonly JsonSerializerOptions LoginJson = new(PergaJsonDefaults.ApiOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false
    };

    public static void MapPergaAuth(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/v1/auth");
        auth.MapPost("/login", LoginAsync).AllowAnonymous();
        auth.MapGet("/session", SessionAsync).RequireAuthorization(PergaAuthPolicies.UserSession);
        auth.MapPost("/logout", LogoutAsync).AllowAnonymous().WithMetadata(new RequestOriginGuard.BrowserWriteApi());
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
        if (!RequestOriginGuard.HasApplicationJson(context.Request))
            return Error(415, "unsupported_media_type", "Login requires application/json.");
        if (context.Request.ContentLength > LoginBodyLimit)
            return BodyTooLarge();

        LoginRequest? request;
        var buffer = new byte[LoginBodyLimit + 1];
        try
        {
            var length = await context.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
            if (length > LoginBodyLimit)
                return BodyTooLarge();
            request = JsonSerializer.Deserialize<LoginRequest>(buffer.AsSpan(0, length), LoginJson);
        }
        catch (JsonException)
        {
            return HttpErrorHandling.BadRequest("invalid_json", "Invalid login request.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        if (string.IsNullOrWhiteSpace(request?.Username) || string.IsNullOrWhiteSpace(request.Password))
            return HttpErrorHandling.BadRequest("invalid_argument", "A username and password are required.");
        if (AuthCredentials.NormalizeUsername(request.Username) is not { } username ||
            !AuthCredentials.IsValidPassword(request.Password, 1))
            return InvalidCredentials();

        var password = request.Password;
        var ip = context.Connection.RemoteIpAddress;
        var ipKey = (ip?.IsIPv4MappedToIPv6 == true ? ip.MapToIPv4() : ip)?.ToString() ?? "loopback";
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
                "Run perga-ctl auth init-owner on the Perga host.");
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

        var expires = now.AddDays(options.AuthSessionLifetimeDays);
        var (cookie, hash) = WebSessionCookie.Create();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var identity = await store.TryCreateSessionIfVersionMatchesAsync(owner, new(
            $"aus_{Guid.NewGuid():N}", hash, now.ToUnixTimeMilliseconds(), expires.ToUnixTimeMilliseconds(),
            userAgent.Length > 512 ? userAgent[..512] : userAgent, ipKey), ct);
        if (identity is null)
            return InvalidCredentials();

        ct.ThrowIfCancellationRequested();
        var attributes = WebSessionCookie.Attributes(context, options);
        attributes.Expires = expires;
        context.Response.Cookies.Append(WebSessionCookie.Name(context, options), cookie, attributes);
        context.Response.Headers[WebSessionCookie.SessionIdHeader] = identity.SessionId;
        await Results.Json(identity, PergaJsonDefaults.ApiOptions).ExecuteAsync(context);
        ct.ThrowIfCancellationRequested();
        attempt.Succeed();
        return null;
    }

    private static async Task<IResult> SessionAsync(HttpContext context, IAuthStore store, TimeProvider time, ILoggerFactory loggerFactory)
    {
        NoStore(context);
        var session = context.Features.Get<AuthenticatedSession>()!.Identity;
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
        TimeProvider time, AuthWebSocketRegistry sockets)
    {
        NoStore(context);
        await context.AuthenticateAsync(PergaAuthSchemes.WebSession);
        var session = context.Features.Get<AuthenticatedSession>();
        if (context.Request.Headers.TryGetValue(WebSessionCookie.SessionIdHeader, out var expected) &&
            (expected.Count != 1 || (session is not null && expected[0] != session.Identity.SessionId)))
            return Results.NoContent();

        if (session is not null)
        {
            await store.RevokeSessionByTokenHashAsync(session.TokenHash, time.GetUtcNow().ToUnixTimeMilliseconds(), CancellationToken.None);
            sockets.DisconnectSession(session.Identity.SessionId);
        }
        context.Response.Cookies.Delete(WebSessionCookie.Name(context, options), WebSessionCookie.Attributes(context, options));
        return Results.NoContent();
    }

    private static IResult BodyTooLarge() => Error(413, "request_body_too_large", "Login requests are limited to 4096 bytes.");
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
