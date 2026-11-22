using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.IntegrationTests.Fixtures;

namespace Perga.Host.IntegrationTests;

public sealed class RequestOriginGuardTests
{
    [Theory]
    [InlineData("/api/v1/areas")]
    [InlineData("/api/v1/today/calendar")]
    [InlineData("/mcp")]
    [InlineData("/mcp/kernel")]
    [InlineData("/api/v1/events")]
    [InlineData("/api/v1/terminals/missing/ws")]
    public async Task ForeignOriginIsRejectedBeforeEndpoint(string path)
    {
        await using var host = new PergaHostTestCluster();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", "https://evil.example");
        using var response = await host.Client.SendAsync(request);
        await AssertForbidden(response, "request_origin_forbidden");
    }

    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://LOCALHOST:80")]
    public async Task SameOriginAndNativeHttpPass(string origin)
    {
        await using var host = new PergaHostTestCluster();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var response = await host.AuthenticatedClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var native = await host.AuthenticatedClient.GetAsync("/api/v1/areas");
        Assert.Equal(HttpStatusCode.OK, native.StatusCode);
        // MCP retains its own capability authentication, rather than being blocked by browser policy.
        using var mcp = await host.Client.GetAsync("/mcp/kernel");
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("/relative")]
    [InlineData("http://localhost/path")]
    [InlineData("http://localhost/.")]
    [InlineData("http://localhost/a/..")]
    [InlineData("http://localhost/%2e")]
    [InlineData("http://localhost\\path")]
    [InlineData("http://user@localhost")]
    [InlineData("http://localhost?query")]
    [InlineData("http://localhost#fragment")]
    [InlineData("http://localhost:81")]
    [InlineData("https://localhost")]
    [InlineData("http://localhost, http://evil.example")]
    [InlineData("http://localhost http://evil.example")]
    public async Task InvalidOrDifferentOriginFailsClosed(string origin)
    {
        await using var host = new PergaHostTestCluster();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await host.Client.SendAsync(request);
        await AssertForbidden(response, "request_origin_forbidden");
    }

    [Fact]
    public async Task EmptyAndRepeatedHeadersFailClosedInServerPipeline()
    {
        await using var host = new PergaHostTestCluster();
        foreach (var values in new[] { new[] { "" }, new[] { "http://localhost", "https://evil.example" } })
        {
            var response = await host.Factory.Server.SendAsync(context =>
            {
                context.Request.Method = "GET";
                context.Request.Scheme = "http";
                context.Request.Host = new HostString("localhost");
                context.Request.Path = "/api/v1/areas";
                context.Request.Headers["Origin"] = values;
            });
            Assert.Equal(403, response.Response.StatusCode);
        }
        foreach (var values in new[] { new[] { "" }, new[] { "same-origin", "cross-site" } })
        {
            var response = await host.Factory.Server.SendAsync(context =>
            {
                context.Request.Method = "GET";
                context.Request.Scheme = "http";
                context.Request.Host = new HostString("localhost");
                context.Request.Path = "/api/v1/areas";
                context.Request.Headers["Sec-Fetch-Site"] = values;
            });
            Assert.Equal(403, response.Response.StatusCode);
        }
    }

    [Theory]
    [InlineData(null, "cross-site")]
    [InlineData("http://localhost", "cross-site")]
    [InlineData(null, "cross-site, same-origin")]
    [InlineData(null, "unknown")]
    public async Task FetchMetadataFailsClosed(string? origin, string site)
    {
        await using var host = new PergaHostTestCluster();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Headers.Add("Sec-Fetch-Site", site);
        using var response = await host.Client.SendAsync(request);
        await AssertForbidden(response, "request_origin_forbidden");
    }

    [Theory]
    [InlineData("evil.example", false)]
    [InlineData("localhost.evil.example", false)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.2:8080", true)]
    [InlineData("[::1]:8080", true)]
    public async Task LocalHostPolicy(string authority, bool allowed)
    {
        await using var host = new PergaHostTestCluster();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
        request.Headers.Host = authority;
        using var response = await host.AuthenticatedClient.SendAsync(request);
        if (allowed) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        else await AssertForbidden(response, "request_host_forbidden");
    }

    [Fact]
    public async Task ExplicitListsBindAndPermitOnlyTheirOwnBoundary()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Perga:AllowedHosts:0"] = "perga.example",
            ["Perga:AllowedOrigins:0"] = "https://console.example:443"
        }).Build();
        var options = PergaHostOptions.BindFrom(configuration, Directory.GetCurrentDirectory());
        await using var host = new PergaHostTestCluster(configureHostOptions: o =>
        {
            o.AllowedHosts = options.AllowedHosts;
            o.AllowedOrigins = options.AllowedOrigins;
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
        request.Headers.Host = "perga.example";
        request.Headers.Add("Origin", "https://console.example");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var response = await host.AuthenticatedClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        request.Headers.Host = "evil.example";
        using var hostDenied = await host.AuthenticatedClient.SendAsync(Clone(request));
        await AssertForbidden(hostDenied, "request_host_forbidden");
        request.Headers.Host = "perga.example";
        request.Headers.Remove("Origin");
        using var noOrigin = await host.AuthenticatedClient.SendAsync(Clone(request));
        await AssertForbidden(noOrigin, "request_origin_forbidden");
        request.Headers.Add("Origin", "https://other.example");
        using var originDenied = await host.AuthenticatedClient.SendAsync(Clone(request));
        await AssertForbidden(originDenied, "request_origin_forbidden");
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/")]
    [InlineData("/assets/app.js")]
    [InlineData("/mcplease")]
    public async Task UnprotectedPathsBypassGuard(string path)
    {
        await using var host = new PergaHostTestCluster();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = "evil.example";
        request.Headers.Add("Origin", "null");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var response = await host.Client.SendAsync(request);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        if (path == "/healthz") Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SameOriginWebSocketHandshakePasses()
    {
        await using var host = new PergaHostTestCluster();
        var client = host.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["Origin"] = "http://localhost";
            request.Headers.Cookie = host.SessionCookie;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/api/v1/events"), timeout.Token);
        using var frame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, timeout.Token);
        Assert.Equal("_replay_complete", frame.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ExplicitOriginAndHostPermitWebSocketHandshake()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: o =>
        {
            o.AllowedHosts = ["perga.example"];
            o.AllowedOrigins = ["https://console.example"];
        });
        var client = host.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers["Origin"] = "https://console.example";
            request.Headers.Cookie = host.SessionCookie;
            request.Headers["Sec-Fetch-Site"] = "cross-site";
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await client.ConnectAsync(new Uri("ws://perga.example/api/v1/events"), timeout.Token);
        using var frame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, timeout.Token);
        Assert.Equal("_replay_complete", frame.RootElement.GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("/api/v1/events")]
    [InlineData("/api/v1/terminals/missing/ws")]
    public async Task ForeignWebSocketHandshakeReturns403(string path)
    {
        await using var host = new PergaHostTestCluster();
        var client = host.Factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Origin"] = "https://evil.example";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ConnectAsync(new Uri("ws://localhost" + path), timeout.Token));
        Assert.Contains("403", error.Message);
    }

    [Fact]
    public async Task DrainStillRequiresOwnerBearerAndRejectedWritesHaveNoEffect()
    {
        await using var host = new PergaHostTestCluster();
        host.Client.DefaultRequestHeaders.Remove("Origin");
        host.Client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var denied = await host.Client.PostAsJsonAsync("/api/v1/system/drain", new { draining = true });
        await AssertForbidden(denied, "system_management_forbidden");
        host.ManagementClient.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        using var foreign = await host.ManagementClient.PostAsJsonAsync("/api/v1/system/drain", new { draining = true });
        await AssertForbidden(foreign, "request_origin_forbidden");
        host.ManagementClient.DefaultRequestHeaders.Remove("Origin");
        host.ManagementClient.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var info = JsonDocument.Parse(await host.ManagementClient.GetStringAsync("/api/v1/system/info"));
        Assert.False(info.RootElement.GetProperty("draining").GetBoolean());
        using var permitted = await host.ManagementClient.PostAsJsonAsync("/api/v1/system/drain", new { draining = true });
        Assert.Equal(HttpStatusCode.Accepted, permitted.StatusCode);
    }

    [Theory]
    [InlineData("drain")]
    [InlineData("reload")]
    public async Task AllowedOriginDoesNotRelaxOwnerAuthorization(string action)
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: o =>
            o.AllowedOrigins = ["https://console.example"]);
        host.Client.DefaultRequestHeaders.Add("Origin", "https://console.example");
        using var response = await host.Client.PostAsJsonAsync("/api/v1/system/" + action,
            new { draining = true, releaseId = "missing", webBuildId = "missing" });
        await AssertForbidden(response, "system_management_forbidden");
    }

    [Fact]
    public async Task ForwardedHeadersCannotApproveForeignHostOrOrigin()
    {
        await using var host = new PergaHostTestCluster();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
        request.Headers.Host = "evil.example";
        request.Headers.Add("Forwarded", "for=127.0.0.1;host=localhost;proto=http");
        request.Headers.Add("X-Forwarded-Host", "localhost");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        request.Headers.Add("Origin", "http://localhost");
        using var response = await host.Client.SendAsync(request);
        await AssertForbidden(response, "insecure_transport_required");
        request.Headers.Host = "localhost";
        request.Headers.Remove("Origin");
        request.Headers.Add("Origin", "https://evil.example");
        using var originDenied = await host.Client.SendAsync(Clone(request));
        await AssertForbidden(originDenied, "insecure_transport_required");
    }

    [Theory]
    [InlineData("https", "localhost:443", "https://LOCALHOST")]
    [InlineData("http", "[::1]:80", "http://[::1]")]
    public async Task CanonicalHttpsAndIpv6OriginsPass(string scheme, string authority, string origin)
    {
        await using var host = new PergaHostTestCluster();
        using var client = scheme == "https"
            ? await BrowserAuthentication.CreateAuthenticatedClientAsync(host.Factory, new Uri("https://localhost"))
            : null;
        var cookie = client?.DefaultRequestHeaders.GetValues("Cookie").Single() ?? host.SessionCookie;
        var context = await host.Factory.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Scheme = scheme;
            ctx.Request.Host = new HostString(authority);
            ctx.Request.Path = "/api/v1/areas";
            ctx.Request.Headers["Origin"] = origin;
            ctx.Request.Headers.Cookie = cookie;
        });
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Fact]
    public async Task RealLoopbackSocketEnforcesHostAndOriginBeforeHttpAndWebSocket()
    {
        var directory = Directory.CreateTempSubdirectory("perga-origin-socket-");
        try
        {
            await using var factory = new IsolatedPergaHostFactory(directory.FullName);
            factory.UseKestrel(0);
            using var original = factory.CreateClient();
            using var client = await BrowserAuthentication.CreateAuthenticatedClientAsync(factory, original.BaseAddress);
            var origin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
            using var sameOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
            sameOrigin.Headers.Add("Origin", origin);
            using var allowed = await client.SendAsync(sameOrigin);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
            rebinding.Headers.Host = "evil.example";
            using var deniedHost = await client.SendAsync(rebinding);
            await AssertForbidden(deniedHost, "request_host_forbidden");
            using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
            foreign.Headers.Add("Origin", "https://evil.example");
            using var deniedOrigin = await client.SendAsync(foreign);
            await AssertForbidden(deniedOrigin, "request_origin_forbidden");
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", origin);
            socket.Options.SetRequestHeader("Cookie", client.DefaultRequestHeaders.GetValues("Cookie").Single());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var wsUri = new UriBuilder(client.BaseAddress) { Scheme = "ws", Path = "/api/v1/events" }.Uri;
            await socket.ConnectAsync(wsUri, timeout.Token);
            using var frame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, timeout.Token);
            Assert.Equal("_replay_complete", frame.RootElement.GetProperty("kind").GetString());
            using var foreignSocket = new ClientWebSocket();
            foreignSocket.Options.SetRequestHeader("Origin", "https://evil.example");
            var error = await Assert.ThrowsAsync<WebSocketException>(() => foreignSocket.ConnectAsync(wsUri, timeout.Token));
            Assert.Contains("403", error.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("192.0.2.1", true)]
    public async Task HostPolicyUsesAcceptedLocalAddress(string localIp, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/areas";
        context.Request.Host = new HostString("evil.example");
        context.Connection.LocalIpAddress = IPAddress.Parse(localIp);
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.2");
        context.Response.Body = new MemoryStream();
        var called = false;
        var guard = new RequestOriginGuard(_ => { called = true; return Task.CompletedTask; }, new());
        // Provide JSON result services for rejection, using the real host's service provider.
        await using var host = new PergaHostTestCluster();
        context.RequestServices = host.Services;
        await guard.InvokeAsync(context);
        Assert.Equal(allowed, called);
        if (!allowed) Assert.Equal(403, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("*.example", false)]
    [InlineData("localhost:80", false)]
    [InlineData("https://example/path", true)]
    [InlineData("null", true)]
    public void InvalidAllowlistFailsAtStartup(string entry, bool origin)
    {
        var options = new PergaHostOptions();
        if (origin) options.AllowedOrigins = [entry];
        else options.AllowedHosts = [entry];
        Assert.Throws<InvalidOperationException>(() => new RequestOriginGuard(_ => Task.CompletedTask, options));
    }

    private static async Task AssertForbidden(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
}
