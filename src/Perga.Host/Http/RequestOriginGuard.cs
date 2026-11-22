using Microsoft.Net.Http.Headers;
using Perga.Host.Configuration;

namespace Perga.Host.Http;

/// <summary>Browser-origin and loopback DNS-rebinding boundary; not client authentication.</summary>
public sealed class RequestOriginGuard
{
    private static readonly object DisallowedWrite = new();
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _allowedHosts;
    private readonly HashSet<string> _allowedOrigins;
    private readonly bool _proxied;
    private readonly string? _publicOrigin;

    public RequestOriginGuard(RequestDelegate next, PergaHostOptions options)
    {
        _next = next;
        _proxied = options.AuthIngressMode == AuthIngressMode.Proxied;
        _allowedHosts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var host in options.AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
                throw new InvalidOperationException("Perga:AllowedHosts entries must be bare host names or IP addresses.");
            _allowedHosts.Add(host.Trim('[', ']'));
        }
        _allowedOrigins = new(StringComparer.Ordinal);
        foreach (var origin in options.AllowedOrigins)
        {
            if (!TryOrigin(origin, out var authority))
                throw new InvalidOperationException("Perga:AllowedOrigins entries must be HTTP(S) origins.");
            _allowedOrigins.Add(authority);
        }

        if (options.AuthPublicOrigin is { } publicOrigin && TryOrigin(publicOrigin, out var configuredOrigin))
        {
            _publicOrigin = configuredOrigin;
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var isApi = request.Path.StartsWithSegments("/api");
        if (!isApi && !request.Path.StartsWithSegments("/mcp"))
        {
            await _next(context);
            return;
        }

        // Use the accepted socket's local address, never attacker-controlled Host or forwarded headers.
        // In-process transports have no socket address and retain the strict local policy.
        var local = context.Connection.LocalIpAddress;
        var host = request.Host.Host.Trim('[', ']');
        var ownOrigin = TryOrigin($"{request.Scheme}://{request.Host}", out var authority) ? authority : null;
        var allowedHost = _allowedHosts.Contains(host) || ownOrigin is not null && ownOrigin == _publicOrigin;
        var isDirectLoopbackMcp = !isApi &&
            context.Features.Get<RawTransport>() is { } raw && raw.IsDirectLoopback(context) &&
            RawTransport.IsLoopbackHost(host) &&
            !request.Headers.ContainsKey("Origin") &&
            !request.Headers.Keys.Any(name => name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase));
        if (!allowedHost && (_proxied || RawTransport.IsLoopback(local) && !RawTransport.IsLoopbackHost(host)) &&
            !TransportSecurityMiddleware.IsVerifiedLoopbackManagement(context) &&
            !isDirectLoopbackMcp)
        {
            await HttpErrorHandling.Forbidden("request_host_forbidden", "Request Host is not allowed for this ingress.").ExecuteAsync(context);
            return;
        }

        var explicitlyAllowed = false;
        var hasOrigin = false;
        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1 || !TryOrigin(origins[0], out var origin))
            {
                await RejectOrigin(context);
                return;
            }
            explicitlyAllowed = _allowedOrigins.Contains(origin);
            hasOrigin = true;
            if (!explicitlyAllowed && origin != ownOrigin && origin != _publicOrigin)
            {
                await RejectOrigin(context);
                return;
            }
        }

        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var sites) &&
            (sites.Count != 1 || sites[0] is not ("same-origin" or "same-site" or "none" or "cross-site") ||
             sites[0] == "cross-site" && !explicitlyAllowed))
        {
            await RejectOrigin(context);
            return;
        }
        var upgrade = request.Headers.GetCommaSeparatedValues(HeaderNames.Upgrade).Any(value => string.Equals(value, "websocket", StringComparison.OrdinalIgnoreCase)) ||
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpExtendedConnectFeature>() is { IsExtendedConnect: true, Protocol: "websocket" };
        if (upgrade && !hasOrigin && isApi)
        {
            await RejectOrigin(context);
            return;
        }

        if (isApi && IsWrite(request.Method) &&
            !HasApplicationJson(request) && !hasOrigin && !HasAllowedReferer(request, ownOrigin))
            context.Items[DisallowedWrite] = true;

        await _next(context);
    }

    internal static Task EnforceWriteSourceAsync(HttpContext context, RequestDelegate next) =>
        context.Items.ContainsKey(DisallowedWrite) &&
        context.GetEndpoint()?.Metadata.GetMetadata<BrowserWriteApi>() is not null
            ? RejectOrigin(context)
            : next(context);

    internal sealed class BrowserWriteApi;

    private bool HasAllowedReferer(HttpRequest request, string? ownOrigin) =>
        request.Headers.Referer.Count == 1 &&
        Uri.TryCreate(request.Headers.Referer[0], UriKind.Absolute, out var referer) &&
        referer.IsWellFormedOriginalString() && referer.UserInfo.Length == 0 &&
        TryOrigin(referer.GetLeftPart(UriPartial.Authority), out var origin) &&
        (origin == ownOrigin || origin == _publicOrigin || _allowedOrigins.Contains(origin));

    internal static bool HasApplicationJson(HttpRequest request) =>
        MediaTypeHeaderValue.TryParse(request.ContentType, out var type) &&
        type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsWrite(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static Task RejectOrigin(HttpContext context) =>
        HttpErrorHandling.Forbidden("request_origin_forbidden", "Request browser origin is not allowed.").ExecuteAsync(context);

    internal static bool TryOrigin(string? value, out string authority)
    {
        authority = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString() ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        // Uri normalizes dot segments; validate the original path before that can erase them.
        var pathStart = value.IndexOf('/', uri.Scheme.Length + 3);
        if (pathStart >= 0 && pathStart != value.Length - 1) return false;
        authority = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
