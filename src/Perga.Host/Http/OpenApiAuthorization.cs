using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Perga.Host.Auth;
using Perga.Host.Configuration;

namespace Perga.Host.Http;

/// <summary>Projects registered auth policies into documentation; never participates in enforcement.</summary>
internal static class OpenApiAuthorization
{
    private const string Cookie = "pergaSessionCookie";
    private const string Management = "systemManagementBearer";

    public static JsonArray CookieSecurity() => new(new JsonObject { [Cookie] = new JsonArray() });

    public static JsonArray Security(IServiceProvider services, Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return endpoint.Metadata.GetMetadata<OptionalWebSession>() is null ? new JsonArray()
                : new JsonArray(new JsonObject { [Cookie] = new JsonArray() }, new JsonObject());
        var metadata = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        if (metadata.Count == 0) return CookieSecurity(); // Program's fail-closed synthetic endpoint guard.
        var provider = services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = AuthorizationPolicy.CombineAsync(provider, metadata).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("Missing endpoint authorization policy.");
        var result = new JsonArray();
        foreach (var scheme in policy.AuthenticationSchemes.Distinct())
        {
            var key = scheme switch
            {
                PergaAuthSchemes.WebSession => Cookie,
                PergaAuthSchemes.SystemManagement => Management,
                _ => throw new InvalidOperationException($"Unsupported published authentication scheme: {scheme}")
            };
            result.Add(new JsonObject { [key] = new JsonArray() });
        }
        if (result.Count == 0) throw new InvalidOperationException("Published policy has no declared authentication scheme.");
        return result;
    }

    public static JsonObject Schemes()
    {
        var http = new DefaultHttpContext();
        var https = new DefaultHttpContext();
        https.Request.Scheme = "https";
        var options = new PergaHostOptions();
        return new JsonObject
        {
            [Cookie] = new JsonObject { ["type"] = "apiKey", ["in"] = "cookie",
                ["name"] = WebSessionCookie.Name(http, options),
                ["description"] = $"HTTP session cookie; HTTPS uses {WebSessionCookie.Name(https, options)}." },
            [Management] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" }
        };
    }
}
