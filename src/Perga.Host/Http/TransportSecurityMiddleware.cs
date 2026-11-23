using System.Net;
using Perga.Host.Configuration;

namespace Perga.Host.Http;

internal sealed record RawTransport(IPAddress? RemoteIp, bool IsHttps, bool HasForwardedHeaders)
{
    public static bool IsLoopback(IPAddress? ip) =>
        ip is null || IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IsLoopback(address);

    public bool IsDirectLoopback(HttpContext context) =>
        !HasForwardedHeaders && IsLoopback(RemoteIp) && IsLoopback(context.Connection.LocalIpAddress);
}

internal sealed class CaptureRawTransportMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Features.Set(new RawTransport(context.Connection.RemoteIpAddress, context.Request.IsHttps,
            context.Request.Headers.Keys.Any(name => name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))));
        return next(context);
    }
}

internal sealed class TransportSecurityMiddleware(
    RequestDelegate next, PergaHostOptions options, SystemManagementAuthorization management)
{
    private static readonly object VerifiedLoopbackManagement = new();
    private readonly bool _allowDirectLoopbackHttp = options.AuthIngressMode == AuthIngressMode.Direct &&
        options.AuthCookieSecurePolicy == AuthCookieSecurePolicy.Auto;
    private readonly bool _allowInsecureHttp = options.AuthAllowInsecureHttp;

    internal static bool IsVerifiedLoopbackManagement(HttpContext context) => context.Items.ContainsKey(VerifiedLoopbackManagement);

    public Task InvokeAsync(HttpContext context)
    {
        var raw = context.Features.Get<RawTransport>()!;
        var secure = context.Request.IsHttps && (raw.IsHttps || options.TrustForwardedHeaders && raw.RemoteIp is not null);
        var loopbackManagement = !_allowDirectLoopbackHttp && management.VerifyDirectLoopbackSystemRequest(context);
        if (loopbackManagement)
            context.Items[VerifiedLoopbackManagement] = true;

        if (context.Request.Path.StartsWithSegments("/api") && !secure && !loopbackManagement &&
            !_allowInsecureHttp && !(_allowDirectLoopbackHttp && raw.IsDirectLoopback(context)))
        {
            var nextStep = _allowDirectLoopbackHttp
                ? "Access Perga over HTTPS, connect directly via http://localhost, or set PERGA_AUTH_ALLOW_INSECURE_HTTP=true on a trusted private network."
                : "Access Perga over HTTPS using direct TLS or a configured trusted reverse proxy.";
            return HttpErrorHandling.Forbidden("insecure_transport_required", "An HTTPS connection is required.", nextStep)
                .ExecuteAsync(context);
        }

        return next(context);
    }
}
