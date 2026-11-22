using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Perga.Contracts;
using Perga.Host.Auth;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.IntegrationTests;

public sealed class AuthHttpIntegrationTests
{
    [Theory]
    [InlineData("/api")]
    [InlineData("/api/v1/bootstrap")]
    [InlineData("/api/v1/today/daily")]
    [InlineData("/api/unknown")]
    [InlineData("/api/unknown.asset")]
    [InlineData("/mcp")]
    [InlineData("/mcp/kernel")]
    [InlineData("/mcp/unknown.asset")]
    public async Task AnonymousProtectedAndUnknownRoutesReturnJson401(string path)
    {
        await using var host = new PergaHostTestCluster();
        using var response = await host.Client.GetAsync(path);
        await AssertErrorAsync(response, 401, "unauthorized");
    }

    [Fact]
    public async Task UserManagementAndMcpCredentialsRemainScopedToTheirPolicies()
    {
        await using var host = new PergaHostTestCluster();
        using var health = await host.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        foreach (var client in new[] { host.AuthenticatedClient, host.ManagementClient })
        {
            using var info = await client.GetAsync("/api/v1/system/info");
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        }

        using var anonymousInfo = await host.Client.GetAsync("/api/v1/system/info");
        await AssertErrorAsync(anonymousInfo, 401, "unauthorized");

        using var managementArea = await host.ManagementClient.GetAsync("/api/v1/areas");
        await AssertErrorAsync(managementArea, 401, "unauthorized");
        using var userMcp = await host.AuthenticatedClient.GetAsync("/mcp/kernel");
        await AssertErrorAsync(userMcp, 401, "unauthorized");
        using var managementMcp = await host.ManagementClient.GetAsync("/mcp/unknown");
        await AssertErrorAsync(managementMcp, 401, "unauthorized");

        var bridge = host.Services.GetRequiredService<IMcpSessionBridge>();
        var mcpToken = bridge.RegisterSession("track", "session", true, null, null, []).Principal.Token;
        using var mcpRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp/unknown")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", mcpToken) }
        };
        using var mcpResponse = await host.Client.SendAsync(mcpRequest);
        await AssertErrorAsync(mcpResponse, 404, "not_found");

        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", mcpToken) }
        };
        using var mcpApi = await host.Client.SendAsync(apiRequest);
        await AssertErrorAsync(mcpApi, 401, "unauthorized");

        using var apiUnknown = await host.AuthenticatedClient.GetAsync("/api/unknown.asset");
        await AssertErrorAsync(apiUnknown, 404, "not_found");
        using var anonymousUnknown = await host.Client.PostAsync("/api/unknown", null);
        await AssertErrorAsync(anonymousUnknown, 401, "unauthorized");
        using var authenticatedUnknown = await host.AuthenticatedClient.PostAsync("/api/unknown", null);
        await AssertErrorAsync(authenticatedUnknown, 404, "not_found");

        using var userAuthStatus = await host.AuthenticatedClient.GetAsync("/api/v1/system/auth-status");
        await AssertErrorAsync(userAuthStatus, 403, "system_management_forbidden");
        Assert.NotNull(await host.ManagementClient.GetFromJsonAsync<SystemAuthStatusDto>("/api/v1/system/auth-status"));
    }

    [Fact]
    public async Task SystemWritesRequireTheManagementBearer()
    {
        await using var host = new PergaHostTestCluster();
        using var userDrain = await host.AuthenticatedClient.PostAsJsonAsync("/api/v1/system/drain", new { draining = true });
        await AssertErrorAsync(userDrain, 403, "system_management_forbidden");
        using var userReload = await host.AuthenticatedClient.PostAsJsonAsync("/api/v1/system/reload", new { releaseId = "a", webBuildId = "b" });
        await AssertErrorAsync(userReload, 403, "system_management_forbidden");

        using var managementDrain = await host.ManagementClient.PostAsJsonAsync("/api/v1/system/drain", new { draining = true });
        Assert.Equal(HttpStatusCode.Accepted, managementDrain.StatusCode);
        var status = await host.ManagementClient.GetFromJsonAsync<SystemAuthStatusDto>("/api/v1/system/auth-status");
        Assert.True(status!.Configured);
        Assert.Equal(BrowserAuthentication.Username, status.Username);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginSessionAndLogoutUseMatchingCookieAttributesAndRejectStaleTab(bool secure)
    {
        await using var host = new PergaHostTestCluster();
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);
        using var browser = host.Factory.CreateDefaultClient(new Uri(secure ? "https://localhost" : "http://localhost"));
        using var first = await LoginAsync(browser, $" \t{BrowserAuthentication.Username.ToUpperInvariant()} \n");
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var original = (await first.Content.ReadFromJsonAsync<SessionIdentityDto>())!;
        using var second = await LoginAsync(browser);
        var identity = (await second.Content.ReadFromJsonAsync<SessionIdentityDto>())!;
        var cookie = BrowserAuthentication.ReadCookie(second);
        Assert.NotEqual(original.SessionId, identity.SessionId);
        Assert.Equal(identity.SessionId, Assert.Single(second.Headers.GetValues("X-Perga-Session-Id")));
        Assert.Equal(BrowserAuthentication.Username, identity.Username);
        Assert.Equal("Test Owner", identity.DisplayName);
        Assert.StartsWith("usr_", identity.UserId);
        Assert.Equal("owner", identity.Role);
        AssertCookie(second, secure);
        Assert.Equal("no-store", second.Headers.CacheControl!.ToString());

        browser.DefaultRequestHeaders.Add("Cookie", cookie);
        using var sessionResponse = await browser.GetAsync("/api/v1/auth/session");
        Assert.Equal("no-store", sessionResponse.Headers.CacheControl!.ToString());
        Assert.Contains("Cookie", sessionResponse.Headers.Vary);
        Assert.Equal(identity, await sessionResponse.Content.ReadFromJsonAsync<SessionIdentityDto>());

        using var staleRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout") { Content = JsonContent.Create(new { }) };
        staleRequest.Headers.Add("X-Perga-Session-Id", original.SessionId);
        using var stale = await browser.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.NoContent, stale.StatusCode);
        Assert.False(stale.Headers.Contains("Set-Cookie"));
        Assert.Equal(identity, await browser.GetFromJsonAsync<SessionIdentityDto>("/api/v1/auth/session"));

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout") { Content = JsonContent.Create(new { }) };
        if (secure)
            logoutRequest.Headers.Add("X-Perga-Session-Id", identity.SessionId);
        using var logout = await browser.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal("no-store", logout.Headers.CacheControl!.ToString());
        AssertCookie(logout, secure);
        Assert.Contains("expires=", Assert.Single(logout.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);

        using var revoked = await browser.GetAsync("/api/v1/auth/session");
        await AssertErrorAsync(revoked, 401, "unauthorized");
    }

    [Fact]
    public async Task UnconfiguredAndMalformedLoginReturnStableErrors()
    {
        await using var host = new PergaHostTestCluster();
        using var login = await LoginAsync(host.Client);
        var notConfigured = await AssertErrorAsync(login, 403, "auth_not_configured");
        Assert.Contains("perga-ctl auth init-owner", notConfigured.NextStep);

        using var wrongType = await host.Client.PostAsync("/api/v1/auth/login", new StringContent("data"));
        await AssertErrorAsync(wrongType, 415, "unsupported_media_type");
        using var extraField = await host.Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "owner", password = "password", extra = true });
        await AssertErrorAsync(extraField, 400, "invalid_json");
        using var tooLarge = await host.Client.PostAsync("/api/v1/auth/login",
            new StringContent(new string('a', 4_097), Encoding.UTF8, "application/json"));
        await AssertErrorAsync(tooLarge, 413, "request_body_too_large");

        using var stream = new MemoryStream(new byte[4_097]);
        using var streamed = new StreamContent(stream);
        streamed.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = streamed };
        request.Headers.TransferEncodingChunked = true;
        using var tooLargeStream = await host.Client.SendAsync(request);
        await AssertErrorAsync(tooLargeStream, 413, "request_body_too_large");
    }

    [Fact]
    public async Task MissingCredentialsAndInvalidCredentialFormatsHaveStableErrors()
    {
        await using var host = new PergaHostTestCluster();
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);

        using var missing = await host.Client.PostAsJsonAsync("/api/v1/auth/login", new { username = "owner" });
        await AssertErrorAsync(missing, 400, "invalid_argument");
        using var blankUsername = await LoginAsync(host.Client, username: " \t");
        await AssertErrorAsync(blankUsername, 400, "invalid_argument");
        using var blankPassword = await LoginAsync(host.Client, password: " \t");
        await AssertErrorAsync(blankPassword, 400, "invalid_argument");

        using var badUsername = await LoginAsync(host.Client, username: "owner name");
        var expected = await AssertErrorAsync(badUsername, 401, "invalid_credentials");
        using var badPassword = await LoginAsync(host.Client, password: "invalid\0password");
        Assert.Equal(expected, await AssertErrorAsync(badPassword, 401, "invalid_credentials"));
        using var wrongPassword = await LoginAsync(host.Client, password: "wrong");
        Assert.Equal(expected, await AssertErrorAsync(wrongPassword, 401, "invalid_credentials"));
    }

    [Fact]
    public async Task ShieldLogsTheCompleteExceptionAndKeepsDetailsOutOfTheResponse()
    {
        var logs = new ErrorLogProvider();
        var hasher = new ThrowingHasher();
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddSingleton<IAuthPasswordHasher>(hasher);
        });
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);

        using var response = await LoginAsync(host.Client);
        var error = await AssertErrorAsync(response, 500, "internal_error");
        Assert.Equal("An unexpected server error occurred.", error.Error);
        Assert.DoesNotContain(hasher.Fault.Message, await response.Content.ReadAsStringAsync());

        var entry = Assert.Single(logs.Errors, log => log.Message == "Unhandled exception processing POST /api/v1/auth/login");
        Assert.Same(hasher.Fault, entry.Error);
        Assert.Contains(nameof(ThrowingHasher.Verify), entry.Error!.StackTrace);
    }

    [Fact]
    public async Task DirectModeRejectsNonLoopbackCleartextAndForwardedClaims()
    {
        await using var host = new PergaHostTestCluster();
        var nonlocal = await host.Factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
            context.Request.Host = new HostString("localhost");
            context.Request.Path = "/api/v1/areas";
        });
        Assert.Equal(403, nonlocal.Response.StatusCode);

        using var spoofed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
        spoofed.Headers.Add("X-Forwarded-Proto", "https");
        using var denied = await host.Client.SendAsync(spoofed);
        var insecure = await AssertErrorAsync(denied, 403, "insecure_transport_required");
        Assert.Contains("HTTPS", insecure.NextStep);
        Assert.Contains("http://localhost", insecure.NextStep);

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, "/api/v1/areas");
        crossSite.Headers.Add("Origin", "http://localhost");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var forbidden = await host.AuthenticatedClient.SendAsync(crossSite);
        await AssertErrorAsync(forbidden, 403, "request_origin_forbidden");

        using var noSource = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        noSource.Headers.Add("Cookie", host.SessionCookie);
        using var writeDenied = await host.Client.SendAsync(noSource);
        await AssertErrorAsync(writeDenied, 403, "request_origin_forbidden");
    }

    [Fact]
    public async Task ProxiedModeRequiresTrustedTlsAndPinsThePublicHost()
    {
        var directory = Directory.CreateTempSubdirectory("perga-auth-proxy-");
        try
        {
            await using var factory = new IsolatedPergaHostFactory(directory.FullName, options =>
            {
                options.AuthIngressMode = AuthIngressMode.Proxied;
                options.AuthPublicOrigin = "https://perga.example";
                options.TrustForwardedHeaders = true;
            }, configureHost: builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders_Enabled"] = "true" })));
            factory.UseKestrel(0);
            using var client = factory.CreateClient();
            await BrowserAuthentication.InitializeOwnerAsync(factory);

            using var cleartextRequest = ProxyRequest("perga.example", tls: false);
            using var unforwarded = await client.SendAsync(cleartextRequest);
            await AssertErrorAsync(unforwarded, 403, "insecure_transport_required");
            using var differentHostRequest = ProxyRequest("different.example", tls: true);
            using var differentHost = await client.SendAsync(differentHostRequest);
            await AssertErrorAsync(differentHost, 403, "request_host_forbidden");

            using var differentPortRequest = ProxyRequest("perga.example:8443", tls: true);
            using var differentPort = await client.SendAsync(differentPortRequest);
            await AssertErrorAsync(differentPort, 403, "request_host_forbidden");

            using var proxiedRequest = ProxyRequest("perga.example", tls: true);
            using var proxiedLogin = await client.SendAsync(proxiedRequest);
            Assert.True(proxiedLogin.IsSuccessStatusCode, await proxiedLogin.Content.ReadAsStringAsync());
            AssertCookie(proxiedLogin, secure: true);
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        var untrustedDirectory = Directory.CreateTempSubdirectory("perga-auth-untrusted-proxy-");
        try
        {
            await using var untrusted = new IsolatedPergaHostFactory(untrustedDirectory.FullName, options =>
            {
                options.AuthIngressMode = AuthIngressMode.Proxied;
                options.AllowedHosts = ["perga.example"];
                options.TrustForwardedHeaders = true;
                options.TrustedProxyAddresses = ["192.0.2.5"];
            }, configureHost: builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders_Enabled"] = "true" })));
            var rejected = await untrusted.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                context.Request.Host = new HostString("perga.example");
                context.Request.Path = "/api/v1/areas";
                context.Request.Headers["X-Forwarded-For"] = "192.0.2.5";
                context.Request.Headers["X-Forwarded-Proto"] = "https";
            });
            Assert.Equal(403, rejected.Response.StatusCode);
        }
        finally
        {
            untrustedDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoopbackManagementRemainsAvailableInStrictTransportModes(bool proxied)
    {
        var directory = Directory.CreateTempSubdirectory("perga-auth-loopback-management-");
        try
        {
            await using var factory = new IsolatedPergaHostFactory(directory.FullName, options =>
            {
                options.AuthCookieSecurePolicy = AuthCookieSecurePolicy.Always;
                if (proxied)
                {
                    options.AuthIngressMode = AuthIngressMode.Proxied;
                    options.AuthPublicOrigin = "https://perga.example";
                    options.TrustForwardedHeaders = true;
                }
            });
            factory.UseKestrel(0);
            using var cleartext = factory.CreateClient();
            await BrowserAuthentication.InitializeOwnerAsync(factory);
            var tokenFile = factory.Services.GetRequiredService<SystemManagementAuthorization>().TokenFilePath;
            var bearer = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(tokenFile)).Trim());
            using var management = factory.CreateClient();
            management.DefaultRequestHeaders.Authorization = bearer;

            using var info = await management.GetAsync("/api/v1/system/info");
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);
            using var drain = await management.PostAsJsonAsync("/api/v1/system/drain", new { draining = true });
            Assert.Equal(HttpStatusCode.Accepted, drain.StatusCode);
            var status = await management.GetFromJsonAsync<SystemAuthStatusDto>("/api/v1/system/auth-status");
            Assert.True(status!.Configured);

            using var anonymousInfo = await cleartext.GetAsync("/api/v1/system/info");
            await AssertErrorAsync(anonymousInfo, 403, "insecure_transport_required");
            using var anonymousLogin = await LoginAsync(cleartext);
            await AssertErrorAsync(anonymousLogin, 403, "insecure_transport_required");
            using var bearerLogin = await LoginAsync(management);
            await AssertErrorAsync(bearerLogin, 403, "insecure_transport_required");

            if (proxied)
            {
                using var proxyRequest = ProxyRequest("perga.example", tls: true);
                using var proxyLogin = await cleartext.SendAsync(proxyRequest);
                Assert.True(proxyLogin.IsSuccessStatusCode, await proxyLogin.Content.ReadAsStringAsync());
                using var cookieRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
                cookieRequest.Headers.Add("Cookie", BrowserAuthentication.ReadCookie(proxyLogin));
                using var cookieInfo = await cleartext.SendAsync(cookieRequest);
                await AssertErrorAsync(cookieInfo, 403, "insecure_transport_required");
            }

            foreach (var header in new[] { "Origin", "Sec-Fetch-Mode", "X-Forwarded-Host" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
                Assert.True(request.Headers.TryAddWithoutValidation(header, "http://localhost"));
                using var rejected = await management.SendAsync(request);
                await AssertErrorAsync(rejected, 403, "insecure_transport_required");
            }

            using var wrongToken = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
            wrongToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 64));
            using var invalid = await management.SendAsync(wrongToken);
            await AssertErrorAsync(invalid, 403, "insecure_transport_required");

            using var wrongHost = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
            wrongHost.Headers.Host = "perga.example";
            using var untrustedHost = await management.SendAsync(wrongHost);
            await AssertErrorAsync(untrustedHost, 403, "insecure_transport_required");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ExplicitAuthOptionsTakePrecedenceOverEnvironmentAliases()
    {
        var aliases = new Dictionary<string, string?>
        {
            ["PERGA_AUTH_INGRESS_MODE"] = "Proxied",
            ["PERGA_AUTH_COOKIE_SECURE"] = "Always",
            ["PERGA_AUTH_SESSION_DAYS"] = "150",
            ["PERGA_AUTH_PUBLIC_ORIGIN"] = "https://alias.example",
            ["PERGA_TRUST_FORWARDED_HEADERS"] = "true"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(aliases);
        var fallback = PergaHostOptions.BindFrom(config.Build(), Directory.GetCurrentDirectory());
        Assert.Equal(AuthIngressMode.Proxied, fallback.AuthIngressMode);
        Assert.Equal(AuthCookieSecurePolicy.Always, fallback.AuthCookieSecurePolicy);
        Assert.Equal(150, fallback.AuthSessionLifetimeDays);
        Assert.Equal("https://alias.example", fallback.AuthPublicOrigin);
        Assert.True(fallback.TrustForwardedHeaders);

        config.AddCommandLine([
            "--Perga:AuthIngressMode=Direct", "--Perga:AuthCookieSecurePolicy=Auto",
            "--Perga:AuthSessionLifetimeDays=45", "--Perga:AuthPublicOrigin=https://explicit.example",
            "--Perga:TrustForwardedHeaders=false"
        ]);
        var explicitOptions = PergaHostOptions.BindFrom(config.Build(), Directory.GetCurrentDirectory());
        Assert.Equal(AuthIngressMode.Direct, explicitOptions.AuthIngressMode);
        Assert.Equal(AuthCookieSecurePolicy.Auto, explicitOptions.AuthCookieSecurePolicy);
        Assert.Equal(45, explicitOptions.AuthSessionLifetimeDays);
        Assert.Equal("https://explicit.example", explicitOptions.AuthPublicOrigin);
        Assert.False(explicitOptions.TrustForwardedHeaders);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(366, 15)]
    [InlineData(30, 0)]
    [InlineData(30, 301)]
    public void InvalidAuthLifetimesFailAtStartup(int days, int seconds)
    {
        var directory = Directory.CreateTempSubdirectory("perga-auth-config-");
        try
        {
            using var factory = new IsolatedPergaHostFactory(directory.FullName, options =>
            {
                options.AuthSessionLifetimeDays = days;
                options.AuthWsRevalidationInterval = TimeSpan.FromSeconds(seconds);
            });
            Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProxiedModeAllowsDirectLoopbackMcpAndRejectsCrossSitePublicOrigin()
    {
        var directory = Directory.CreateTempSubdirectory("perga-auth-proxied-mcp-");
        try
        {
            await using var factory = new IsolatedPergaHostFactory(directory.FullName, options =>
            {
                options.AuthIngressMode = AuthIngressMode.Proxied;
                options.AuthPublicOrigin = "https://perga.example";
                options.TrustForwardedHeaders = true;
            });
            factory.UseKestrel(0);
            using var client = factory.CreateClient();
            await BrowserAuthentication.InitializeOwnerAsync(factory);

            var bridge = factory.Services.GetRequiredService<IMcpSessionBridge>();
            var token = bridge.RegisterSession("trk_1", "ses_1", isLead: true, null, null, []).Principal.Token;

            // Direct loopback MCP request with valid bearer succeeds in Proxied mode.
            using var mcpOk = new HttpRequestMessage(HttpMethod.Post, "/mcp/kernel")
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json")
            };
            mcpOk.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var mcpOkRes = await client.SendAsync(mcpOk);
            Assert.Equal(HttpStatusCode.OK, mcpOkRes.StatusCode);

            // MCP request with browser headers or forwarded headers is rejected in Proxied mode.
            foreach (var (header, value) in new[]
            {
                ("Origin", "http://127.0.0.1"),
                ("Sec-Fetch-Site", "same-origin"),
                ("X-Forwarded-For", "192.0.2.9")
            })
            {
                using var badMcp = new HttpRequestMessage(HttpMethod.Post, "/mcp/kernel")
                {
                    Content = new StringContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""", Encoding.UTF8, "application/json")
                };
                badMcp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                Assert.True(badMcp.Headers.TryAddWithoutValidation(header, value));
                using var badMcpRes = await client.SendAsync(badMcp);
                Assert.Equal(HttpStatusCode.Forbidden, badMcpRes.StatusCode);
            }

            // Direct loopback /api/* with loopback Host remains forbidden in Proxied mode.
            using var directApi = await client.GetAsync("/api/v1/areas");
            Assert.Equal(HttpStatusCode.Forbidden, directApi.StatusCode);

            // Proxied request with Origin == AuthPublicOrigin and Sec-Fetch-Site: cross-site is rejected.
            using var crossSite = ProxyRequest("perga.example", tls: true);
            crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var crossSiteRes = await client.SendAsync(crossSite);
            await AssertErrorAsync(crossSiteRes, 403, "request_origin_forbidden");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UnauthenticatedWrongMethodOnApiAndMcpReturnsUnauthorizedAndStaleLogoutClearsCookie()
    {
        await using var cluster = new PergaHostTestCluster();

        using var wrongApi = await cluster.Client.PutAsync("/api/v1/bootstrap", null);
        await AssertErrorAsync(wrongApi, 401, "unauthorized");

        using var wrongMcp = await cluster.Client.PutAsync("/mcp/kernel", null);
        await AssertErrorAsync(wrongMcp, 401, "unauthorized");

        await BrowserAuthentication.InitializeOwnerAsync(cluster.Factory);
        using var login = await LoginAsync(cluster.Client);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = BrowserAuthentication.ReadCookie(login);
        var sessionId = Assert.Single(login.Headers.GetValues("X-Perga-Session-Id"));

        // Revoke session out-of-band, then send logout with X-Perga-Session-Id: dead cookie must still be cleared.
        var dbPath = cluster.Services.GetRequiredService<ResolvedHostStoragePaths>().DbPath;
        var admin = new SqliteAuthAdministration(dbPath);
        await admin.RevokeAllSessionsAsync(cluster.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(), CancellationToken.None);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("Cookie", cookie);
        logout.Headers.Add("X-Perga-Session-Id", sessionId);
        logout.Headers.Add("Origin", "http://localhost");
        using var logoutRes = await cluster.Client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, logoutRes.StatusCode);
        var setCookie = Assert.Single(logoutRes.Headers.GetValues("Set-Cookie"));
        Assert.Contains("expires=Thu, 01 Jan 1970", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage ProxyRequest(string host, bool tls)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = BrowserAuthentication.Username, password = BrowserAuthentication.Password })
        };
        request.Headers.Host = host;
        if (tls)
        {
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-For", "192.0.2.20");
        }
        request.Headers.Add("Origin", $"https://{host}");
        return request;
    }

    internal static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username = BrowserAuthentication.Username,
        string password = BrowserAuthentication.Password, CancellationToken ct = default) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { username, password }, ct);

    internal static async Task<ApiErrorResponse> AssertErrorAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var error = (await response.Content.ReadFromJsonAsync<ApiErrorResponse>())!;
        Assert.Equal(code, error.Code);
        return error;
    }

    private static void AssertCookie(HttpResponseMessage response, bool secure)
    {
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(secure ? "__Host-perga_session=" : "perga_session=", cookie);
        Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(secure, cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("; domain=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingHasher : IAuthPasswordHasher
    {
        public InvalidOperationException Fault { get; } = new("secret database detail");
        public string Hash(string password) => "unused";
        public PasswordVerificationResult Verify(string password, string encodedHash) => throw Fault;
        public PasswordVerificationResult VerifyDummy(string password) => throw Fault;
    }

    private sealed class ErrorLogProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Message, Exception? Error)> Errors { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(Errors);
        public void Dispose() { }

        private sealed class Recorder(ConcurrentQueue<(string Message, Exception? Error)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    entries.Enqueue((formatter(state, exception), exception));
            }
        }
    }
}
