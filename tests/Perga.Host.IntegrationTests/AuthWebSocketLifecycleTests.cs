using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Perga.Contracts;
using Perga.Host.Auth;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.IntegrationTests;

public sealed class AuthWebSocketLifecycleTests
{
    [Fact]
    public async Task RegistryCompletesNormallyWhenTheHostStopsIt()
    {
        await using var host = new PergaHostTestCluster();
        var registry = Assert.Single(host.Services.GetServices<IHostedService>().OfType<BackgroundService>(),
            service => service.GetType().Name == "AuthWebSocketRegistry");
        var execution = Assert.IsAssignableFrom<Task>(registry.ExecuteTask);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await registry.StopAsync(deadline.Token);

        await execution.WaitAsync(deadline.Token);
    }

    [Theory]
    [InlineData("/api/v1/events")]
    [InlineData("/api/v1/terminals/missing/ws")]
    public async Task UpgradeRequiresBothAnOwnerSessionAndAnOriginBeforeAccept(string path)
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var noSession = host.Factory.Server.CreateWebSocketClient();
        noSession.ConfigureRequest = request => request.Headers.Origin = "http://localhost";
        var unauthorized = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            noSession.ConnectAsync(new Uri("ws://localhost" + path), deadline.Token));
        Assert.Contains("401", unauthorized.Message);

        var noOrigin = host.Factory.Server.CreateWebSocketClient();
        var cookie = host.SessionCookie;
        noOrigin.ConfigureRequest = request => request.Headers.Cookie = cookie;
        var forbidden = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            noOrigin.ConnectAsync(new Uri("ws://localhost" + path), deadline.Token));
        Assert.Contains("403", forbidden.Message);
    }

    [Fact]
    public async Task LogoutImmediatelyClosesEventsAndTerminalWith1008()
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var (track, _) = await host.CreateTrackAsync();
        using var opened = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/tracks/{track}/terminals",
            new { title = "Logout relay", program = "/bin/sh", cols = 80, rows = 24 }, ct);
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync(ct));
        var terminal = (await opened.Content.ReadFromJsonAsync<TerminalDto>(PergaJsonDefaults.ApiOptions, ct))!;

        using var eventsSocket = await host.ConnectEventsWebSocketAsync(ct: ct);
        using var terminalSocket = await host.ConnectTerminalWebSocketAsync(terminal.Id, ct);
        using var logout = await host.AuthenticatedClient.PostAsync("/api/v1/auth/logout", null, ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var prompt = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(AssertRevoked(eventsSocket, prompt.Token), AssertRevoked(terminalSocket, prompt.Token));
    }

    [Fact]
    public async Task CliRevocationClosesExistingSocketsAtRevalidation()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
            options.AuthWsRevalidationInterval = TimeSpan.FromSeconds(1));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await host.ConnectEventsWebSocketAsync(ct: deadline.Token);

        var path = Path.Combine(host.DataDir, "perga.db");
        using var stderr = new StringWriter();
        var rc = await AuthCliCommand.RunAsync(["auth", "revoke-sessions", "--db-path", path],
            TextReader.Null, TextWriter.Null, stderr, deadline.Token);
        Assert.True(rc == 0, stderr.ToString());
        await AssertRevoked(socket, deadline.Token);
    }

    [Fact]
    public async Task AProbeThatFinishesAfterItsDeadlineDoesNotCloseTheSocketOnTheNextTick()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
            options.AuthWsRevalidationInterval = TimeSpan.FromSeconds(3), configureExtraServices: services =>
                services.AddSingleton<IAuthStore, DelayedProbeStore>());
        var store = (DelayedProbeStore)host.Services.GetRequiredService<IAuthStore>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var socket = await host.ConnectEventsWebSocketAsync(ct: deadline.Token);

        var received = AssertRevoked(socket, deadline.Token);
        var completed = await Task.WhenAny(received, store.Recovered.Task).WaitAsync(deadline.Token);
        Assert.Same(store.Recovered.Task, completed);
        using var logout = await host.AuthenticatedClient.PostAsync("/api/v1/auth/logout", null, deadline.Token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        await received;
    }

    [Fact]
    public async Task ExpiredSessionIsRejectedBeforeNewUpgradeAndClosedOnExistingUpgrade()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
        {
            options.AuthSessionLifetimeDays = 1;
            options.AuthWsRevalidationInterval = TimeSpan.FromSeconds(1);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await host.ConnectEventsWebSocketAsync(ct: deadline.Token);
        host.TimeProvider.Advance(TimeSpan.FromDays(1));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.ConnectEventsWebSocketAsync(ct: deadline.Token));
        Assert.Contains("401", failure.Message);
        await AssertRevoked(socket, deadline.Token);
    }

    private static async Task AssertRevoked(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        } while (result.MessageType != WebSocketMessageType.Close);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result.CloseStatus);
        Assert.Equal("session_revoked", result.CloseStatusDescription);
    }

    private sealed class DelayedProbeStore(SqliteTrackStore inner) : IAuthStore
    {
        private int _probes;
        private int _delayedProbeRunning;
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> IsOwnerSessionValidAsync(string id, long nowMs, CancellationToken ct)
        {
            var probe = Interlocked.Increment(ref _probes);
            if (probe == 2)
            {
                Volatile.Write(ref _delayedProbeRunning, 1);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    // Emulate a blocking DB read that outlives its deadline and the start of the next tick.
                    await Task.Delay(1_250, CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                }
                finally
                {
                    Volatile.Write(ref _delayedProbeRunning, 0);
                }
            }
            if (Volatile.Read(ref _delayedProbeRunning) != 0)
                throw new InvalidOperationException("WebSocket revalidation probes must run one at a time per session.");

            var valid = await inner.IsOwnerSessionValidAsync(id, nowMs, ct);
            if (probe > 2 && valid)
                Recovered.TrySetResult();
            return valid;
        }

        public Task<AuthUserRecord?> GetOwnerAsync(CancellationToken ct) => inner.GetOwnerAsync(ct);

        public Task<AuthSetupState> GetSetupStateAsync(CancellationToken ct) => inner.GetSetupStateAsync(ct);
        public Task<InitialOwnerSetupResult> TryCreateInitialOwnerWithSessionAsync(
            NewInitialOwner owner, NewAuthSession session, bool allowExistingBusinessData, CancellationToken ct) =>
            inner.TryCreateInitialOwnerWithSessionAsync(owner, session, allowExistingBusinessData, ct);

        public Task<SessionIdentityDto?> TryCreateSessionIfVersionMatchesAsync(AuthUserRecord owner,
            NewAuthSession session, CancellationToken ct) => inner.TryCreateSessionIfVersionMatchesAsync(owner, session, ct);

        public Task<bool> UpgradePasswordHashIfVersionMatchesAsync(AuthUserRecord owner, string hash, long nowMs,
            CancellationToken ct) => inner.UpgradePasswordHashIfVersionMatchesAsync(owner, hash, nowMs, ct);

        public Task<SessionIdentityDto?> GetValidOwnerSessionByTokenHashAsync(string tokenHash, long nowMs,
            CancellationToken ct) => inner.GetValidOwnerSessionByTokenHashAsync(tokenHash, nowMs, ct);

        public Task TouchSessionLastSeenIfStaleAsync(string id, long nowMs, CancellationToken ct) =>
            inner.TouchSessionLastSeenIfStaleAsync(id, nowMs, ct);

        public Task RevokeSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct) =>
            inner.RevokeSessionByTokenHashAsync(tokenHash, nowMs, ct);

        public Task<SystemAuthStatusDto> GetAuthStatusAsync(long nowMs, CancellationToken ct) => inner.GetAuthStatusAsync(nowMs, ct);
    }
}
