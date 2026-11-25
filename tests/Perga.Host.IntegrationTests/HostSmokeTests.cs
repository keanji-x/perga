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
            var providersToProbe = new (string Command, string Provider, string VersionOutput, string ExpectedVersion, string HelpArgs, string HelpOutput)[]
            {
                ("agy", "acp:antigravity", "90.0.0", "90.0.0", "--help", "Usage of agy: Start the Agent Client Protocol (ACP) stdio server"),
                ("codex", "codex", "codex-cli 90.0.1", "codex-cli 90.0.1", "app-server --help", "Usage: codex app-server"),
                ("opencode", "acp:opencode", "opencode v2.90.2", "opencode v2.90.2", "--help", "OpenCode command line interface"),
                ("claude", "claude", "90.0.3 (Claude Code)", "90.0.3 (Claude Code)", "--help", "Usage: claude")
            };
            var arguments = new string[providersToProbe.Length];
            for (var index = 0; index < providersToProbe.Length; index++)
            {
                var spec = providersToProbe[index];
                var executable = Path.Combine(directory.FullName, spec.Command);
                var helpCheck = spec.HelpArgs == "app-server --help"
                    ? $"if [ \"$1\" = app-server ] && [ \"$2\" = --help ]; then echo '{spec.HelpOutput}'; exit 0; fi"
                    : $"if [ \"$1\" = --help ]; then echo '{spec.HelpOutput}'; exit 0; fi";
                File.WriteAllText(
                    executable,
                    $"#!/bin/sh\nif [ \"$1\" = --version ]; then echo '{spec.VersionOutput}'; exit 0; fi\n{helpCheck}\nexit 1\n");
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                arguments[index] = $"--Perga:ProviderExecutables:{spec.Command}={executable}";
            }

            await using var factory = new IsolatedPergaHostFactory(
                Path.Combine(directory.FullName, "test-data"),
                configureHost: builder => builder.ConfigureAppConfiguration((_, config) => config.AddCommandLine(arguments)));
            using var client = await BrowserAuthentication.CreateAuthenticatedClientAsync(factory);
            using var response = await client.GetAsync("/api/v1/bootstrap?refresh=true");
            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var providers = payload.RootElement.GetProperty("providers").EnumerateArray()
                .ToDictionary(provider => provider.GetProperty("provider").GetString()!);
            foreach (var spec in providersToProbe)
            {
                var provider = providers[spec.Provider];
                Assert.True(provider.GetProperty("available").GetBoolean(), $"Expected provider '{spec.Provider}' to be available. Raw: {provider.GetRawText()}");
                Assert.Equal(spec.ExpectedVersion, provider.GetProperty("version").GetString());
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
