using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests;

[Collection("Shell process environment")]
public sealed class ShellCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_StripsHostEnvironmentAndPreservesDeveloperAndExplicitValues()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var keys = new Dictionary<string, string?>
        {
            ["PERGA_DB_PATH"] = "/simulated/managed/perga.db",
            ["Perga__DataDir"] = "/simulated/managed",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:4240",
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["DOTNET_ROOT"] = "/simulated/dotnet",
            ["VIRTUAL_ENV"] = "/simulated/venv"
        };
        var previous = new Dictionary<string, string?>();
        try
        {
            foreach (var (key, value) in keys)
            {
                previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }

            var runner = new ProcessShellCommandRunner();
            var result = await runner.RunAsync(new ShellCommandRequest(
                """test -z "${PERGA_DB_PATH+x}" && test -z "${Perga__DataDir+x}" && test -z "${ASPNETCORE_URLS+x}" && test -z "${DOTNET_ENVIRONMENT+x}" && test "$DOTNET_ROOT" = /simulated/dotnet && test "$VIRTUAL_ENV" = /simulated/venv && printf '%s' "$GATE_SETTING" """,
                Path.GetTempPath(), TimeSpan.FromSeconds(5),
                new Dictionary<string, string> { ["GATE_SETTING"] = "configured" }));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("configured", result.Stdout);
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    [Fact]
    public async Task RunAsync_TimesOutWhenAChildKeepsOutputPipesOpenAfterShellExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new ProcessShellCommandRunner();
        var result = await runner.RunAsync(new ShellCommandRequest(
            "printf ready; sleep 2 &", Path.GetTempPath(), TimeSpan.FromMilliseconds(500), OutputTailChars: 16));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.InRange(result.DurationMs, 1, 1_500);
        Assert.Equal("ready", result.Stdout);
    }
}

[CollectionDefinition("Shell process environment", DisableParallelization = true)]
public sealed class ShellProcessEnvironmentCollection;
