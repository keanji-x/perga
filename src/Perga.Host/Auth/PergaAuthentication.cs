using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.Mcp;
using Perga.Host.WebSockets;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.Auth;

internal static class PergaAuthSchemes
{
    public const string WebSession = nameof(WebSession);
    public const string SystemManagement = nameof(SystemManagement);
    public const string McpSession = nameof(McpSession);
}

internal static class PergaAuthPolicies
{
    public const string UserSession = nameof(UserSession);
    public const string UserOrManagement = nameof(UserOrManagement);
    public const string SystemManagementOnly = nameof(SystemManagementOnly);
    public const string McpSessionOnly = nameof(McpSessionOnly);
}

internal sealed record AuthenticatedSession(SessionIdentityDto Identity, string TokenHash)
{
    public static InteropPrincipal.Human Human(HttpContext context)
    {
        var session = context.Features.Get<AuthenticatedSession>()!.Identity;
        return new(session.UserId, session.Username);
    }
}

internal static class PergaAuthentication
{
    public static void AddPergaAuthentication(this IServiceCollection services)
    {
        services.TryAddSingleton<IAuthStore>(sp => sp.GetRequiredService<SqliteTrackStore>());
        services.Configure<PasswordHasherOptions>(options =>
        {
            options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            options.IterationCount = AuthPasswordHasher.DefaultIterationCount;
        });
        services.AddOptions<LoginRateLimitOptions>();
        services.TryAddSingleton<IAuthPasswordHasher, AuthPasswordHasher>();
        services.TryAddSingleton<LoginRateLimiter>();
        services.TryAddSingleton<LocalSessionManager>();
        services.AddHostedService<LocalAuthStartup>();
        services.TryAddSingleton<AuthWebSocketRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<AuthWebSocketRegistry>());

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, WebSessionAuthenticationHandler>(PergaAuthSchemes.WebSession, _ => { })
            .AddScheme<AuthenticationSchemeOptions, SystemManagementAuthenticationHandler>(PergaAuthSchemes.SystemManagement, _ => { })
            .AddScheme<AuthenticationSchemeOptions, McpSessionAuthenticationHandler>(PergaAuthSchemes.McpSession, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(PergaAuthPolicies.UserSession, p => p.AddAuthenticationSchemes(PergaAuthSchemes.WebSession)
                .RequireAuthenticatedUser().RequireRole("owner"))
            .AddPolicy(PergaAuthPolicies.UserOrManagement, p => p.AddAuthenticationSchemes(PergaAuthSchemes.WebSession, PergaAuthSchemes.SystemManagement)
                .RequireAuthenticatedUser())
            .AddPolicy(PergaAuthPolicies.SystemManagementOnly, p => p.AddAuthenticationSchemes(PergaAuthSchemes.SystemManagement)
                .RequireAuthenticatedUser())
            .AddPolicy(PergaAuthPolicies.McpSessionOnly, p => p.AddAuthenticationSchemes(PergaAuthSchemes.McpSession)
                .RequireAuthenticatedUser());
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, PergaAuthorizationResultHandler>();
    }

    public static AuthenticateResult Ticket(string scheme, params Claim[] claims) =>
        AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, scheme)), scheme));
}

internal sealed class WebSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IAuthStore store, TimeProvider time, PergaHostOptions hostOptions, LocalSessionManager localSessions, AuthWebSocketRegistry sockets)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (WebSessionCookie.ReadHash(Context, hostOptions) is not { } hash)
            return AuthenticateResult.NoResult();
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        var session = await store.GetValidOwnerSessionByTokenHashAsync(hash, nowMs, Context.RequestAborted);
        if (session is null && localSessions.IsActive && !hostOptions.RequireAuth &&
            LocalSessionManager.IsDirectLoopback(Context, hostOptions) &&
            !await localSessions.CheckOwnerAsync(store, sockets, Context.RequestAborted))
            session = localSessions.TryGetValidSession(hash, nowMs);
        if (session is null)
            return AuthenticateResult.NoResult();

        Context.Features.Set(new AuthenticatedSession(session, hash));
        return PergaAuthentication.Ticket(Scheme.Name, new Claim(ClaimTypes.NameIdentifier, session.UserId),
            new Claim(ClaimTypes.Name, session.Username), new Claim(ClaimTypes.Role, session.Role));
    }
}

internal sealed class SystemManagementAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    SystemManagementAuthorization authorization) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(authorization.Verify(Context)
        ? PergaAuthentication.Ticket(Scheme.Name)
        : AuthenticateResult.NoResult());
}

internal sealed class McpSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IMcpSessionBridge bridge) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = McpStreamableHttpEndpoints.AuthenticateCaller(Context, bridge);
        if (principal is null)
            return Task.FromResult(AuthenticateResult.NoResult());
        Context.Features.Set(principal);
        return Task.FromResult(PergaAuthentication.Ticket(Scheme.Name));
    }
}

internal sealed class PergaAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Succeeded)
            return next(context);

        context.Response.Headers.CacheControl = "no-store";
        if (policy.AuthenticationSchemes.Count == 1 && policy.AuthenticationSchemes[0] == PergaAuthSchemes.SystemManagement)
            return HttpErrorHandling.Forbidden("system_management_forbidden", "Installation owner authorization is required.")
                .ExecuteAsync(context);
        return HttpErrorHandling.Unauthorized().ExecuteAsync(context);
    }
}
