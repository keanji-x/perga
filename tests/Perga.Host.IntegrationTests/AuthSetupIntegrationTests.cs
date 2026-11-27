using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.Auth;
using Perga.Host.Configuration;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Auth;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.IntegrationTests;

public sealed class AuthSetupIntegrationTests
{
    private const string SetupPath = "/api/v1/auth/setup";
    private const string LocalPath = "/api/v1/auth/local";
    private const string SessionPath = "/api/v1/auth/session";
    private const string Password = "first owner test password";

    [Fact]
    public async Task RestartWithCliOwnerRemovesTokenLeftByAbortedHost()
    {
        using var directory = new TemporaryData();
        string path;
        string token;
        await using (var host = CreateHost(directory.Path))
        {
            path = TokenPath(host);
            token = await File.ReadAllTextAsync(path);
        }

        // Restore the file that would remain if the process had exited without shutdown cleanup.
        await File.WriteAllTextAsync(path, token);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var input = new StringReader(Password + "\n");
        using var error = new StringWriter();
        Assert.True(await AuthCliCommand.RunAsync(
            ["auth", "init-owner", "--username", "cli-owner", "--password-stdin", "--data-dir", directory.Path],
            input, TextWriter.Null, error) == 0, error.ToString());
        Assert.True(File.Exists(path));

        await using var restarted = CreateHost(directory.Path);
        Assert.False(File.Exists(path));
        using var stale = await restarted.Client.PostAsJsonAsync(LocalPath, new { token });
        await AuthHttpIntegrationTests.AssertErrorAsync(stale, 409, "owner_already_configured");
    }

    [Theory]
    [InlineData("https://allowed.example", null)]
    [InlineData("https://allowed.example", "cross-site")]
    [InlineData("http://localhost", "cross-site")]
    public async Task AllowedOriginsCannotBypassLocalSourceChecks(string origin, string? fetchSite)
    {
        await using var host = CreateHost(configureHostOptions: options => options.AllowedOrigins = [origin]);
        var token = await File.ReadAllTextAsync(TokenPath(host));
        using var local = await host.Client.PostAsJsonAsync(LocalPath, new { token });
        var cookie = BrowserAuthentication.ReadCookie(local);

        using var launch = new HttpRequestMessage(HttpMethod.Get, $"/auth/local?token={token}");
        using var exchange = new HttpRequestMessage(HttpMethod.Post, LocalPath) { Content = JsonContent.Create(new { token }) };
        using var setup = new HttpRequestMessage(HttpMethod.Post, SetupPath)
        {
            Content = JsonContent.Create(new { username = "owner", password = Password })
        };
        setup.Headers.Add("Cookie", cookie);
        foreach (var request in new[] { launch, exchange, setup })
        {
            request.Headers.Add("Origin", origin);
            if (fetchSite is not null)
                request.Headers.Add("Sec-Fetch-Site", fetchSite);
            using var rejected = await host.Client.SendAsync(request);
            await AuthHttpIntegrationTests.AssertErrorAsync(rejected, 403, "request_origin_forbidden");
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
        }
        Assert.True((await StatusAsync(host.Client, cookie)).CanSetupInBrowser);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveStartupLeavesNonRegularTokenPathUntouched(bool symlink)
    {
        if (symlink && OperatingSystem.IsWindows())
            return;
        using var directory = new TemporaryData();
        var runtime = Directory.CreateDirectory(Path.Combine(directory.Path, "runtime"));
        var tokenPath = Path.Combine(runtime.FullName, "local-launch-token");
        var target = Path.Combine(directory.Path, "target");
        if (symlink)
        {
            await File.WriteAllTextAsync(target, new string('a', 64));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.CreateSymbolicLink(tokenPath, target);
        }
        else
            Directory.CreateDirectory(tokenPath);

        await using var host = CreateHost(directory.Path, options => options.RequireAuth = true);
        if (symlink)
        {
            Assert.Equal(target, new FileInfo(tokenPath).LinkTarget);
            Assert.True(File.Exists(target));
        }
        else
            Assert.True(Directory.Exists(tokenPath));
    }

    [Fact]
    public async Task LocalLaunchRequiresTokenAndDirectConnectionAndCleansUpOnShutdown()
    {
        using var directory = new TemporaryData();
        string tokenPath;
        await using (var host = CreateHost(explicitDataDir: directory.Path))
        {
            tokenPath = TokenPath(host);
            var token = await File.ReadAllTextAsync(tokenPath);
            Assert.Matches("^[a-f0-9]{64}$", token);
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                Assert.Equal(mode, File.GetUnixFileMode(tokenPath));
                Assert.Equal(mode | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(tokenPath)!));
            }

            Assert.Equal(AuthSetupMode.LocalTokenRequired, (await StatusAsync(host.Client)).Mode);
            using (var anonymous = await host.Client.GetAsync("/api/v1/bootstrap"))
                await AuthHttpIntegrationTests.AssertErrorAsync(anonymous, 401, "unauthorized");
            using (var anonymous = await host.Client.GetAsync("/mcp/unknown"))
                await AuthHttpIntegrationTests.AssertErrorAsync(anonymous, 401, "unauthorized");
            using (var setup = await SetupAsync(host.Client))
                await AuthHttpIntegrationTests.AssertErrorAsync(setup, 403, "local_session_required");

            foreach (var header in new[] { ("Sec-Fetch-Site", "cross-site"), ("X-Forwarded-For", "127.0.0.1") })
            {
                using var link = new HttpRequestMessage(HttpMethod.Get, $"/auth/local?token={token}");
                link.Headers.Add(header.Item1, header.Item2);
                using var rejectedLink = await host.Client.SendAsync(link);
                Assert.Equal(HttpStatusCode.Forbidden, rejectedLink.StatusCode);
                Assert.False(rejectedLink.Headers.Contains("Set-Cookie"));

                using var paste = new HttpRequestMessage(HttpMethod.Post, LocalPath) { Content = JsonContent.Create(new { token }) };
                paste.Headers.Add(header.Item1, header.Item2);
                using var rejectedPaste = await host.Client.SendAsync(paste);
                Assert.Equal(HttpStatusCode.Forbidden, rejectedPaste.StatusCode);
                Assert.False(rejectedPaste.Headers.Contains("Set-Cookie"));
            }

            using var invalid = await host.Client.GetAsync("/auth/local?token=wrong");
            Assert.Equal(HttpStatusCode.Found, invalid.StatusCode);
            Assert.False(invalid.Headers.Contains("Set-Cookie"));
            using var linkSuccess = await host.Client.GetAsync($"/auth/local?token={token}");
            Assert.Equal(HttpStatusCode.Found, linkSuccess.StatusCode);
            Assert.Equal("/", linkSuccess.Headers.Location!.OriginalString);
            Assert.Equal("no-referrer", Assert.Single(linkSuccess.Headers.GetValues("Referrer-Policy")));
            Assert.True(linkSuccess.Headers.CacheControl!.NoStore);
            var attributes = Assert.Single(linkSuccess.Headers.GetValues("Set-Cookie"));
            Assert.Contains("httponly", attributes, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", attributes, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", attributes, StringComparison.OrdinalIgnoreCase);
            using var session = await GetAsync(host.Client, SessionPath, BrowserAuthentication.ReadCookie(linkSuccess));
            Assert.Equal(AuthSessionMode.Local, (await ReadAsync<SessionIdentityDto>(session)).Mode);
            using var repeatedLink = await GetAsync(host.Client, $"/auth/local?token={token}", BrowserAuthentication.ReadCookie(linkSuccess));
            Assert.Equal(HttpStatusCode.Found, repeatedLink.StatusCode);
            Assert.False(repeatedLink.Headers.Contains("Set-Cookie"));

            using var pasteSuccess = await host.Client.PostAsJsonAsync(LocalPath, new { token });
            Assert.Equal(HttpStatusCode.OK, pasteSuccess.StatusCode);
            var identity = await ReadAsync<SessionIdentityDto>(pasteSuccess);
            Assert.Equal(AuthSessionMode.Local, identity.Mode);
            Assert.Equal(identity.SessionId, Assert.Single(pasteSuccess.Headers.GetValues("X-Perga-Session-Id")));
        }
        Assert.False(File.Exists(tokenPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenExchangeDisconnectsTheEvictedOrExpiredLocalSocket(bool expire)
    {
        await using var host = CreateHost(configureHostOptions: options =>
        {
            options.AuthSessionLifetimeDays = 1;
            options.AuthWsRevalidationInterval = TimeSpan.FromMinutes(5);
        });
        var token = await File.ReadAllTextAsync(TokenPath(host));
        using var first = await host.Client.PostAsJsonAsync(LocalPath, new { token });
        var firstCookie = BrowserAuthentication.ReadCookie(first);
        var removedCookie = firstCookie;
        if (!expire)
        {
            using var second = await host.Client.PostAsJsonAsync(LocalPath, new { token });
            removedCookie = BrowserAuthentication.ReadCookie(second);
        }
        var wsClient = host.Factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => { request.Headers.Origin = "http://localhost"; request.Headers.Cookie = removedCookie; };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await wsClient.ConnectAsync(new Uri("ws://localhost/api/v1/events"), deadline.Token);

        if (expire)
            host.TimeProvider.Advance(TimeSpan.FromDays(2));
        else
        {
            for (var i = 2; i < 16; i++)
            {
                using var launch = await host.Client.PostAsJsonAsync(LocalPath, new { token });
                launch.EnsureSuccessStatusCode();
            }
            using var refreshed = await GetAsync(host.Client, SessionPath, firstCookie);
            refreshed.EnsureSuccessStatusCode();
        }

        using var replacement = await host.Client.PostAsJsonAsync(LocalPath, new { token });
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        if (!expire)
        {
            using var stillValid = await GetAsync(host.Client, SessionPath, firstCookie);
            stillValid.EnsureSuccessStatusCode();
        }
        using var removed = await GetAsync(host.Client, SessionPath, removedCookie);
        await AuthHttpIntegrationTests.AssertErrorAsync(removed, 401, "unauthorized");
        await AssertSocketRevokedAsync(socket, deadline.Token);
    }

    [Fact]
    public async Task LocalSetupPreservesAreaAndTrackAndRevokesEveryLocalSessionAndSocket()
    {
        var responsePause = new PauseSetupResponse();
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
            options.AuthWsRevalidationInterval = TimeSpan.FromMinutes(5),
            configureExtraServices: services => services.AddSingleton<IStartupFilter>(responsePause));
        var token = await File.ReadAllTextAsync(TokenPath(host));
        using var first = await host.Client.PostAsJsonAsync(LocalPath, new { token });
        using var second = await host.Client.PostAsJsonAsync(LocalPath, new { token });
        var cookie = BrowserAuthentication.ReadCookie(first);
        var other = BrowserAuthentication.ReadCookie(second);
        using var area = await PostAsync(host.Client, "/api/v1/areas", new { name = "Existing area", defaultCwd = host.DataDir }, cookie);
        area.EnsureSuccessStatusCode();
        var areaId = (await ReadAsync<AreaDto>(area)).Id;
        using var track = await PostAsync(host.Client, "/api/v1/tracks", new { areaId, cwd = host.DataDir }, cookie);
        track.EnsureSuccessStatusCode();
        var trackId = (await ReadAsync<TrackAggregateSnapshot>(track)).Track.Id;
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);

        var wsClient = host.Factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => { request.Headers.Origin = "http://localhost"; request.Headers.Cookie = other; };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await wsClient.ConnectAsync(new Uri("ws://localhost/api/v1/events"), deadline.Token);
        Assert.True((await StatusAsync(host.Client, cookie)).CanSetupInBrowser);

        var setupRequest = SetupAsync(host.Client, cookie);
        try
        {
            await responsePause.Started.Task.WaitAsync(deadline.Token);
            Assert.Equal(AuthSetupMode.Configured, (await StatusAsync(host.Client)).Mode);
            Assert.True(File.Exists(TokenPath(host)));
        }
        finally
        {
            responsePause.Release.TrySetResult();
        }

        using var setup = await setupRequest;
        Assert.Equal(HttpStatusCode.Created, setup.StatusCode);
        var identity = await ReadAsync<SessionIdentityDto>(setup);
        Assert.Equal(AuthSessionMode.Owner, identity.Mode);
        Assert.Equal(identity.SessionId, Assert.Single(setup.Headers.GetValues("X-Perga-Session-Id")));
        foreach (var revoked in new[] { cookie, other })
        {
            using var response = await GetAsync(host.Client, SessionPath, revoked);
            await AuthHttpIntegrationTests.AssertErrorAsync(response, 401, "unauthorized");
        }
        await AssertSocketRevokedAsync(socket, deadline.Token);
        Assert.False(File.Exists(TokenPath(host)));

        var ownerCookie = BrowserAuthentication.ReadCookie(setup);
        using var restored = await GetAsync(host.Client, $"/api/v1/tracks/{trackId}", ownerCookie);
        restored.EnsureSuccessStatusCode();
        Assert.Equal(areaId, (await ReadAsync<TrackAggregateSnapshot>(restored)).Track.AreaId);
        using var oldToken = await host.Client.PostAsJsonAsync(LocalPath, new { token });
        await AuthHttpIntegrationTests.AssertErrorAsync(oldToken, 409, "owner_already_configured");
    }

    [Fact]
    public async Task StalledSetupResponseStillRevokesLocalSocketAfterCommit()
    {
        var responsePause = new PauseSetupResponse();
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
            options.AuthWsRevalidationInterval = TimeSpan.FromMinutes(5),
            configureExtraServices: services => services.AddSingleton<IStartupFilter>(responsePause));
        var token = await File.ReadAllTextAsync(TokenPath(host));
        using var local = await host.Client.PostAsJsonAsync(LocalPath, new { token });
        var cookie = BrowserAuthentication.ReadCookie(local);
        var wsClient = host.Factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => { request.Headers.Origin = "http://localhost"; request.Headers.Cookie = cookie; };
        using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await wsClient.ConnectAsync(new Uri("ws://localhost/api/v1/events"), startupDeadline.Token);

        var setupRequest = SetupAsync(host.Client, cookie);
        try
        {
            await responsePause.Started.Task.WaitAsync(startupDeadline.Token);
            using var revocationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await AssertSocketRevokedAsync(socket, revocationDeadline.Token);
            Assert.False(File.Exists(TokenPath(host)));
            Assert.Equal(AuthSetupMode.Configured, (await StatusAsync(host.Client)).Mode);
        }
        finally
        {
            responsePause.Release.TrySetResult();
            using var response = await setupRequest;
        }
    }

    [Fact]
    public async Task RequiredAuthCreatesOneOwnerAndRestartsInPasswordMode()
    {
        using var directory = new TemporaryData();
        SessionIdentityDto identity;
        string cookie;
        await using (var host = CreateHost(directory.Path, options => options.RequireAuth = true))
        {
            Assert.False(File.Exists(TokenPath(host)));
            Assert.Equal(new(AuthSetupMode.SetupRequired, true, true), await StatusAsync(host.Client));

            using var bad = await PostAsync(host.Client, SetupPath, new { username = "owner", password = "😀😀😀😀😀😀" });
            await AuthHttpIntegrationTests.AssertErrorAsync(bad, 400, "invalid_argument");

            var attempts = await Task.WhenAll(SetupAsync(host.Client), SetupAsync(host.Client));
            using var winner = Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Created);
            using var loser = Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Conflict);
            await AuthHttpIntegrationTests.AssertErrorAsync(loser, 409, "owner_already_configured");
            identity = await ReadAsync<SessionIdentityDto>(winner);
            Assert.Equal("owner", identity.Username);
            Assert.Equal("My Owner", identity.DisplayName);
            cookie = BrowserAuthentication.ReadCookie(winner);
        }

        await using var restarted = CreateHost(directory.Path);
        Assert.False(File.Exists(TokenPath(restarted)));
        Assert.Equal(AuthSetupMode.Configured, (await StatusAsync(restarted.Client)).Mode);
        using var persisted = await GetAsync(restarted.Client, SessionPath, cookie);
        Assert.Equal(identity, await ReadAsync<SessionIdentityDto>(persisted));
        using var freshLogin = await AuthHttpIntegrationTests.LoginAsync(restarted.Client, "owner", Password);
        freshLogin.EnsureSuccessStatusCode();
        using var configured = await SetupAsync(restarted.Client);
        await AuthHttpIntegrationTests.AssertErrorAsync(configured, 409, "owner_already_configured");
    }

    [Fact]
    public async Task RequiredAuthDoesNotClaimExistingDataOrExposeItThroughProxy()
    {
        using var directory = new TemporaryData();
        await using (var host = CreateHost(directory.Path))
        {
            using var exchange = await host.Client.PostAsJsonAsync(LocalPath, new { token = await File.ReadAllTextAsync(TokenPath(host)) });
            using var area = await PostAsync(host.Client, "/api/v1/areas", new { name = "Existing", defaultCwd = host.DataDir },
                BrowserAuthentication.ReadCookie(exchange));
            area.EnsureSuccessStatusCode();
        }

        await using (var host = CreateHost(directory.Path, options => options.RequireAuth = true))
        {
            Assert.Equal(AuthSetupBlockedReason.ExistingData, (await StatusAsync(host.Client)).SetupBlockedReason);
            using var rejected = await SetupAsync(host.Client);
            await AuthHttpIntegrationTests.AssertErrorAsync(rejected, 403, "owner_setup_requires_cli");
        }

        await using (var host = CreateHost(directory.Path, options =>
        {
            options.RequireAuth = true;
            options.AuthIngressMode = AuthIngressMode.Proxied;
            options.AuthAllowInsecureHttp = true;
            options.AllowedHosts = ["localhost"];
        }))
        {
            Assert.Equal(AuthSetupBlockedReason.NonLoopback, (await StatusAsync(host.Client)).SetupBlockedReason);
            using var rejected = await SetupAsync(host.Client);
            await AuthHttpIntegrationTests.AssertErrorAsync(rejected, 403, "owner_setup_loopback_required");
        }
    }

    [Theory]
    [InlineData("a bad name", "Good name", Password, OwnerCredentials.UsernameError)]
    [InlineData("owner", "   ", Password, OwnerCredentials.DisplayNameError)]
    [InlineData("owner", "Good name", "😀😀😀😀😀😀", null)]
    public async Task CliAndWebRejectTheSameInvalidCredentials(
        string username, string displayName, string password, string? expected)
    {
        await using var host = CreateHost(configureHostOptions: options => options.RequireAuth = true);
        var db = Path.Combine(host.DataDir, "perga.db");
        using var input = new StringReader(password + "\n");
        using var error = new StringWriter();
        Assert.Equal(1, await AuthCliCommand.RunAsync(
            ["auth", "init-owner", "--username", username, "--display-name", displayName, "--password-stdin", "--db-path", db],
            input, TextWriter.Null, error));
        var rule = expected ?? OwnerCredentials.PasswordError;
        Assert.Equal(rule, error.ToString().Trim());

        using var rejected = await PostAsync(host.Client, SetupPath, new { username, displayName, password });
        var body = await AuthHttpIntegrationTests.AssertErrorAsync(rejected, 400, "invalid_argument");
        Assert.Equal(rule, body.Error);
    }

    [Fact]
    public async Task OwnerCreatedByCliRevokesAnIdleLocalSocketAndToken()
    {
        await using var host = CreateHost(configureHostOptions: options =>
            options.AuthWsRevalidationInterval = TimeSpan.FromMinutes(5));
        using var token = await host.Client.PostAsJsonAsync(LocalPath, new { token = await File.ReadAllTextAsync(TokenPath(host)) });
        var cookie = BrowserAuthentication.ReadCookie(token);
        var wsClient = host.Factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => { request.Headers.Origin = "http://localhost"; request.Headers.Cookie = cookie; };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await wsClient.ConnectAsync(new Uri("ws://localhost/api/v1/events"), deadline.Token);
        using var input = new StringReader(Password + "\n");
        using var error = new StringWriter();
        var code = await AuthCliCommand.RunAsync(
            ["auth", "init-owner", "--username", "cli-owner", "--password-stdin", "--data-dir", host.DataDir],
            input, TextWriter.Null, error);
        Assert.True(code == 0, error.ToString());

        await AssertSocketRevokedAsync(socket, deadline.Token);
        Assert.False(File.Exists(TokenPath(host)));
        using var stale = await GetAsync(host.Client, SessionPath, cookie);
        await AuthHttpIntegrationTests.AssertErrorAsync(stale, 401, "unauthorized");
    }

    [Fact]
    public async Task CliAndWebShareTheSingleOwnerSlot()
    {
        await using var host = CreateHost(configureHostOptions: options => options.RequireAuth = true);
        var db = Path.Combine(host.DataDir, "perga.db");
        using var cliError = new StringWriter();
        using var cliInput = new StringReader(Password + "\n");
        var cli = Task.Run(() => AuthCliCommand.RunAsync(
            ["auth", "init-owner", "--username", "cli-owner", "--password-stdin", "--db-path", db],
            cliInput, TextWriter.Null, cliError));
        using var web = await SetupAsync(host.Client);
        var code = await cli;
        Assert.Equal(code == 0 ? HttpStatusCode.Conflict : HttpStatusCode.Created, web.StatusCode);
        var status = await new SqliteAuthAdministration(db).GetStatusAsync(host.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(), default);
        Assert.Equal(code == 0 ? "cli-owner" : "owner", status.Username);
        Assert.Equal(code == 0 ? 0 : 1, status.ActiveSessions);
    }

    private static PergaHostTestCluster CreateHost(
        string? explicitDataDir = null, Action<PergaHostOptions>? configureHostOptions = null) =>
        new(explicitDataDir, configureHostOptions);

    private static async Task AssertSocketRevokedAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        WebSocketReceiveResult closed;
        do { closed = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct); }
        while (closed.MessageType != WebSocketMessageType.Close);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, closed.CloseStatus);
        Assert.Equal("session_revoked", closed.CloseStatusDescription);
    }

    private static string TokenPath(PergaHostTestCluster host) => Path.Combine(host.DataDir, "runtime", "local-launch-token");

    private static Task<HttpResponseMessage> SetupAsync(HttpClient client, string? cookie = null) =>
        PostAsync(client, SetupPath, new { username = " Owner ", displayName = " My Owner ", password = Password }, cookie);

    private static async Task<AuthSetupStatusDto> StatusAsync(HttpClient client, string? cookie = null)
    {
        using var response = await GetAsync(client, SetupPath, cookie);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        return await ReadAsync<AuthSetupStatusDto>(response);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(PergaJsonDefaults.ApiOptions))!;

    private sealed class TemporaryData : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perga-auth-setup-");
        public string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class PauseSetupResponse : IStartupFilter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, proceed) =>
            {
                if (context.Request.Method == "POST" && context.Request.Path == SetupPath)
                {
                    context.Response.OnStarting(() =>
                    {
                        Started.TrySetResult();
                        return Release.Task;
                    });
                }
                return proceed(context);
            });
            next(app);
        };
    }
}
