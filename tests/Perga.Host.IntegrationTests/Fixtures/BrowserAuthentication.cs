using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Perga.Host.Auth;
using Perga.Host.Configuration;
using Perga.Storage.Sqlite;
using Xunit;

namespace Perga.Host.IntegrationTests.Fixtures;

internal static class BrowserAuthentication
{
    public const string Username = "test-owner";
    public const string Password = "local test password";

    public static async Task InitializeOwnerAsync(WebApplicationFactory<Program> factory, string username = Username,
        string password = Password, string displayName = "Test Owner")
    {
        var dbPath = factory.Services.GetRequiredService<ResolvedHostStoragePaths>().DbPath;
        var store = new SqliteAuthAdministration(dbPath);
        var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeMilliseconds();
        if (!(await store.GetStatusAsync(now, default)).Configured)
        {
            var hash = factory.Services.GetRequiredService<IAuthPasswordHasher>().Hash(password);
            await store.CreateInitialOwnerAsync(username, displayName, hash, now, default);
        }
    }

    public static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory, Uri? baseAddress = null)
    {
        await InitializeOwnerAsync(factory);
        var client = baseAddress is null
            ? factory.CreateDefaultClient(new SameOriginWriteHandler())
            : factory.CreateDefaultClient(baseAddress, new SameOriginWriteHandler());
        try
        {
            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = Username, password = Password });
            Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Add("Cookie", ReadCookie(login));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static string ReadCookie(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];

    private sealed class SameOriginWriteHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options &&
                !request.Headers.Contains("Origin"))
                request.Headers.Add("Origin", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            return base.SendAsync(request, cancellationToken);
        }
    }
}
