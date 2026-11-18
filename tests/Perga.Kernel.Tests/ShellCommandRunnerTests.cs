using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests;

public sealed class ShellCommandRunnerTests
{
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
