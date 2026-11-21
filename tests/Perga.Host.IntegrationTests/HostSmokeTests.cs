using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Perga.Host.IntegrationTests.Fixtures;

namespace Perga.Host.IntegrationTests;

public sealed class HostSmokeTests
{
    [Fact]
    public async Task HealthzEndpoint_ReturnsOk()
    {
        var directory = Directory.CreateTempSubdirectory("perga-host-smoke-");
        try
        {
            await using var factory = new IsolatedPergaHostFactory(directory.FullName);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/healthz");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Bootstrap_ProbesProviderExecutablesThroughOarBridge()
    {
        if (!OperatingSystem.IsLinux()) return;

        var directory = Directory.CreateTempSubdirectory("perga-oar-provider-paths-");
        try
        {
            var commands = new[] { "agy", "codex", "opencode", "claude" };
            var arguments = new string[commands.Length];
            for (var index = 0; index < commands.Length; index++)
            {
                var name = commands[index];
                var executable = Path.Combine(directory.FullName, name);
                File.WriteAllText(
                    executable,
                    $"#!/bin/sh\nif [ \"$1\" = --version ]; then echo '90.0.{index}'; exit 0; fi\nif [ \"$1\" = app-server ] && [ \"$2\" = --help ]; then echo 'usage: codex app-server'; exit 0; fi\nexit 1\n");
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                arguments[index] = $"--Perga:ProviderExecutables:{name}={executable}";
            }

            await using var factory = new IsolatedPergaHostFactory(
                Path.Combine(directory.FullName, "test-data"),
                configureHost: builder => builder.ConfigureAppConfiguration((_, config) => config.AddCommandLine(arguments)));
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
                Assert.True(provider.GetProperty("available").GetBoolean(), $"Expected provider '{providerNames[index]}' to be available. Raw: {provider.GetRawText()}");
                Assert.Equal($"90.0.{index}", provider.GetProperty("version").GetString());
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
