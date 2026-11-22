using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Perga.Host.Auth;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;
using System.Net.Http.Json;

namespace Perga.Host.IntegrationTests;

public sealed class AuthPasswordHasherTests
{
    [Fact]
    public void IdentityV3HashAndDummyHashUseConfiguredIterations()
    {
        var hasher = Hasher(2_000);
        var hash = hasher.Hash(BrowserAuthentication.Password);
        Assert.Equal(1, Convert.FromBase64String(hash)[0]);
        Assert.NotEqual(hash, hasher.Hash(BrowserAuthentication.Password));
        Assert.Equal(PasswordVerificationResult.Success, hasher.Verify(BrowserAuthentication.Password, hash));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.Verify("wrong", hash));
        Assert.Equal(1, Convert.FromBase64String(hasher.DummyHash)[0]);
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyDummy(BrowserAuthentication.Password));
    }

    [Fact]
    public async Task LoginUpgradesLegacyHashWithoutChangingSessionGeneration()
    {
        await using var host = new PergaHostTestCluster();
        var store = host.Services.GetRequiredService<IAuthStore>();
        var admin = new SqliteAuthAdministration(Path.Combine(host.DataDir, "perga.db"));
        var legacy = Hasher(100).Hash(BrowserAuthentication.Password);
        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, Hasher(1_000).Verify(BrowserAuthentication.Password, legacy));
        Assert.Equal(PasswordVerificationResult.Success, Hasher(100).Verify(BrowserAuthentication.Password, Hasher(1_000).Hash(BrowserAuthentication.Password)));
        Assert.True(await admin.CreateInitialOwnerAsync(BrowserAuthentication.Username, "Owner", legacy,
            host.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(), default));
        var before = (await store.GetOwnerAsync(default))!;

        using var response = await host.Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = BrowserAuthentication.Username, password = BrowserAuthentication.Password });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var after = (await store.GetOwnerAsync(default))!;
        Assert.NotEqual(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.SessionGeneration, after.SessionGeneration);
        Assert.Equal(before.CredentialVersion, after.CredentialVersion);
        Assert.Equal(PasswordVerificationResult.Success, Hasher(1_000).Verify(BrowserAuthentication.Password, after.PasswordHash));
    }

    private static AuthPasswordHasher Hasher(int iterations) => new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3, IterationCount = iterations
    }));
}
