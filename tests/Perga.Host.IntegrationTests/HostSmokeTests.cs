using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Perga.Host.IntegrationTests;

public sealed class HostSmokeTests
{
    [Fact]
    public async Task HealthzEndpoint_ReturnsOk()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StartupRejectsInvalidProviderPermissionDefault()
    {
        string[] arguments = ["--Perga:PermissionModeDefaults:codex=always"];
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddCommandLine(arguments)));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Perga:PermissionModeDefaults:codex", error.Message);
        Assert.Contains("never, ask, full", error.Message);
    }

    [Fact]
    public async Task Bootstrap_ProbesProviderExecutablesFromHostConfiguration()
    {
        if (!OperatingSystem.IsLinux()) return;

        var directory = Directory.CreateTempSubdirectory("perga-provider-paths-");
        try
        {
            var commands = new[] { "agy", "codex", "opencode", "claude" };
            var arguments = new string[commands.Length];
            for (var index = 0; index < commands.Length; index++)
            {
                var name = commands[index];
                var executable = Path.Combine(directory.FullName, name);
                File.WriteAllText(executable, $"#!/bin/sh\n[ \"$1\" = --version ] || exit 1\necho '90.0.{index}'\n");
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                arguments[index] = $"--Perga:ProviderExecutables:{name}={executable}";
            }

            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddCommandLine(arguments)));
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/bootstrap?refresh=true");
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var providers = payload.RootElement.GetProperty("providers").EnumerateArray()
                .ToDictionary(provider => provider.GetProperty("provider").GetString()!);
            var providerNames = new[] { "acp:antigravity", "codex", "acp:opencode", "claude" };
            for (var index = 0; index < providerNames.Length; index++)
            {
                var provider = providers[providerNames[index]];
                Assert.True(provider.GetProperty("available").GetBoolean());
                Assert.Equal($"90.0.{index}", provider.GetProperty("version").GetString());
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
