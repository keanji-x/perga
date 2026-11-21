using Perga.TermClient;

namespace Perga.TermClient.Tests;

[Collection("Terminal bridge process environment")]
public sealed class TerminalBridgeDaemonEnvironmentTests
{
    [Fact]
    public async Task ManagedDaemonStripsHostStorageSettingsAndPreservesDeveloperEnv()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var keys = new Dictionary<string, string?>
        {
            ["PERGA_DB_PATH"] = "/simulated/inherited.db",
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:4240",
            ["VIRTUAL_ENV"] = "/simulated/venv"
        };
        var previous = new Dictionary<string, string?>();
        var directory = Directory.CreateTempSubdirectory("perga-daemon-env-");
        try
        {
            var executable = Path.Combine(directory.FullName, "test-terminal-bridge");
            File.WriteAllText(executable,
                """
                #!/bin/sh
                if test -n "${PERGA_DB_PATH+x}" || test -n "${ASPNETCORE_URLS+x}"; then
                    echo 'Host setting reached managed daemon.' >&2
                    exit 1
                fi
                if test "$VIRTUAL_ENV" != /simulated/venv; then
                    echo 'Developer env was stripped from managed daemon.' >&2
                    exit 1
                fi
                echo PERGA_TERM_BRIDGE_TCP=http://127.0.0.1:65535
                exec sleep 10
                """.ReplaceLineEndings("\n"));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var (key, value) in keys)
            {
                previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }

            await using var daemon = await TerminalBridgeDaemon.StartAsync(new TerminalBridgeDaemonOptions(
                ExecutablePath: executable, ListenTcpAddress: "127.0.0.1:0", BuildFromSourceIfMissing: false));

            Assert.Equal(65535, daemon.PreferredEndpointUri.Port);
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
            directory.Delete(recursive: true);
        }
    }
}

[CollectionDefinition("Terminal bridge process environment", DisableParallelization = true)]
public sealed class TerminalBridgeProcessEnvironmentCollection;
