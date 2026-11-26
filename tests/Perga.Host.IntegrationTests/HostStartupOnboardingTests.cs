using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Perga.Contracts;
using Perga.Host.Cli;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class HostStartupOnboardingTests
{
    [Fact]
    public async Task StartupOnboardingSkipsNonInteractiveTestingEphemeralAndExistingOwner()
    {
        var authStore = new FakeAuthStore(owner: null);
        var called = false;
        Task<int> Runner(string[] _, TextReader __, TextWriter ___, TextWriter ____, CancellationToken _____)
        {
            called = true;
            return Task.FromResult(0);
        }

        Assert.Equal(0, await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, "/tmp/perga.db", isEphemeral: false, isTestingEnvironment: false, isInteractive: false,
            TextReader.Null, TextWriter.Null, TextWriter.Null, Runner));
        Assert.Equal(0, await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, "/tmp/perga.db", isEphemeral: false, isTestingEnvironment: true, isInteractive: true,
            TextReader.Null, TextWriter.Null, TextWriter.Null, Runner));
        Assert.Equal(0, await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, "/tmp/perga.db", isEphemeral: true, isTestingEnvironment: false, isInteractive: true,
            TextReader.Null, TextWriter.Null, TextWriter.Null, Runner));
        Assert.Equal(0, authStore.GetOwnerCalls);

        var withOwner = new FakeAuthStore(new AuthUserRecord("usr_1", "owner", "Owner", "owner", "hash", 1, 1, null));
        Assert.Equal(0, await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            withOwner, "/tmp/perga.db", isEphemeral: false, isTestingEnvironment: false, isInteractive: true,
            TextReader.Null, TextWriter.Null, TextWriter.Null, Runner));
        Assert.Equal(1, withOwner.GetOwnerCalls);
        Assert.False(called);
    }

    [Theory]
    [InlineData("/tmp/perga.db", "/tmp/perga.db")]
    [InlineData("/tmp/owner's $active.db", "'/tmp/owner'\\''s $active.db'")]
    public async Task StartupOnboardingPromptsInteractivelyAndDelegatesToAuthCli(string dbPath, string pathArgument)
    {
        var authStore = new FakeAuthStore(owner: null);

        // Declining prints hint and continues startup with exit 0.
        var declineOut = new StringWriter();
        var declineCalled = false;
        var declineCode = await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, dbPath, isEphemeral: false, isTestingEnvironment: false, isInteractive: true,
            new StringReader("n\n"), declineOut, TextWriter.Null,
            (_, _, _, _, _) => { declineCalled = true; return Task.FromResult(0); });
        Assert.Equal(0, declineCode);
        Assert.False(declineCalled);
        Assert.Contains($"No owner account is configured yet for {dbPath}.", declineOut.ToString());
        Assert.Contains($"perga auth init-owner --username owner --allow-migrate --db-path {pathArgument}'", declineOut.ToString());

        // Accepting with Enter + empty username defaults to 'owner' and delegates to AuthCliCommand.
        string[]? capturedArgs = null;
        var acceptOut = new StringWriter();
        var acceptCode = await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, dbPath, isEphemeral: false, isTestingEnvironment: false, isInteractive: true,
            new StringReader("\n\n"), acceptOut, TextWriter.Null,
            (args, _, _, _, _) => { capturedArgs = args; return Task.FromResult(0); });
        Assert.Equal(0, acceptCode);
        Assert.NotNull(capturedArgs);
        Assert.Equal(["auth", "init-owner", "--username", "owner", "--allow-migrate", "--db-path", dbPath], capturedArgs);

        // Custom username + non-zero exit code propagates cleanly.
        var failCode = await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, dbPath, isEphemeral: false, isTestingEnvironment: false, isInteractive: true,
            new StringReader("yes\nalice\n"), new StringWriter(), TextWriter.Null,
            (args, _, _, _, _) => { capturedArgs = args; return Task.FromResult(1); });
        Assert.Equal(1, failCode);
        Assert.NotNull(capturedArgs);
        Assert.Equal(["auth", "init-owner", "--username", "alice", "--allow-migrate", "--db-path", dbPath], capturedArgs);
    }

    [Fact]
    public void FormatStartupErrorHandlesPortInUseAndPermissionDenied()
    {
        var addressInUse = HostStartupOnboarding.FormatStartupError(
            new IOException("Failed to bind", new AddressInUseException("port busy")));
        Assert.NotNull(addressInUse);
        Assert.Contains("error: Could not start Perga because the address/port is already in use.", addressInUse);
        Assert.Contains("perga --urls http://127.0.0.1:4042", addressInUse);

        var socketInUse = HostStartupOnboarding.FormatStartupError(
            new IOException("Failed to bind", new SocketException((int)SocketError.AddressAlreadyInUse)));
        Assert.Equal(addressInUse, socketInUse);

        var permission = HostStartupOnboarding.FormatStartupError(new UnauthorizedAccessException("Access denied"));
        Assert.NotNull(permission);
        Assert.Contains("insufficient permissions", permission);
        Assert.Contains("perga --urls http://127.0.0.1:4041", permission);

        var socketAccess = HostStartupOnboarding.FormatStartupError(
            new SocketException((int)SocketError.AccessDenied));
        Assert.NotNull(socketAccess);
        Assert.Contains("insufficient permissions", socketAccess);

        Assert.Null(HostStartupOnboarding.FormatStartupError(new InvalidOperationException("foreign_key_check failed")));
        Assert.Null(HostStartupOnboarding.FormatStartupError(new ArgumentOutOfRangeException("unrelated")));
    }

    [Fact]
    public void FormatConfigurationErrorHandlesBindingAndValidationFailures()
    {
        foreach (var exception in new Exception[]
                 {
                     new FormatException("Invalid integer value"),
                     new InvalidOperationException("Invalid auth policy"),
                     new ArgumentException("Invalid auth enum"),
                     new OverflowException("Integer value out of range")
                 })
        {
            var message = HostStartupOnboarding.FormatConfigurationError(exception);
            Assert.StartsWith("error: Invalid Perga configuration:", message);
            Assert.Contains(exception.Message, message);
        }
    }

    private sealed class FakeAuthStore(AuthUserRecord? owner) : IAuthStore
    {
        public int GetOwnerCalls { get; private set; }

        public Task<AuthUserRecord?> GetOwnerAsync(CancellationToken ct)
        {
            GetOwnerCalls++;
            return Task.FromResult(owner);
        }

        public Task<SessionIdentityDto?> TryCreateSessionIfVersionMatchesAsync(AuthUserRecord ownerRecord, NewAuthSession session, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> UpgradePasswordHashIfVersionMatchesAsync(AuthUserRecord ownerRecord, string passwordHash, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SessionIdentityDto?> GetValidOwnerSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> IsOwnerSessionValidAsync(string sessionId, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task TouchSessionLastSeenIfStaleAsync(string sessionId, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RevokeSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SystemAuthStatusDto> GetAuthStatusAsync(long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
