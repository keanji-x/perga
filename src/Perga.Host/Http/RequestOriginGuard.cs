using System.Net;
using Perga.Host.Configuration;

namespace Perga.Host.Http;

/// <summary>Browser-origin and loopback DNS-rebinding boundary; not client authentication.</summary>
public sealed class RequestOriginGuard
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _allowedHosts;
    private readonly HashSet<string> _allowedOrigins;

    public RequestOriginGuard(RequestDelegate next, PergaHostOptions options)
    {
        _next = next;
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
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase) &&
            !request.Path.StartsWithSegments("/api/today", StringComparison.OrdinalIgnoreCase) &&
            !request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Use the accepted socket's local address, never attacker-controlled Host or forwarded headers.
        // In-process transports have no socket address and retain the strict local policy.
        var local = context.Connection.LocalIpAddress;
        var host = request.Host.Host.Trim('[', ']');
        if ((local is null || IsLoopback(local)) &&
            !host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !(IPAddress.TryParse(host, out var address) && IsLoopback(address)) && !_allowedHosts.Contains(host))
        {
            await HttpErrorHandling.Forbidden("request_host_forbidden", "Request Host is not allowed for a loopback connection.").ExecuteAsync(context);
            return;
        }

        var explicitlyAllowed = false;
        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1 || !TryOrigin(origins[0], out var origin))
            {
                await RejectOrigin(context);
                return;
            }
            explicitlyAllowed = _allowedOrigins.Contains(origin);
            if (!explicitlyAllowed &&
                (!TryOrigin($"{request.Scheme}://{request.Host}", out var ownOrigin) || origin != ownOrigin))
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
        await _next(context);
    }

    private static Task RejectOrigin(HttpContext context) =>
        HttpErrorHandling.Forbidden("request_origin_forbidden", "Request browser origin is not allowed.").ExecuteAsync(context);

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static bool TryOrigin(string? value, out string authority)
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
