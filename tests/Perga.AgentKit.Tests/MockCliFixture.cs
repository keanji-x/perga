using System.Text;

namespace Perga.AgentKit.Tests;

public sealed class MockCliFixture : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // The build copies these executables before parallel tests can launch subprocesses.
    private static readonly string MockCliDir = Path.Combine(AppContext.BaseDirectory, "MockCli");

    public string RootDir { get; }
    public string WorkspaceDir { get; }
    public string MockCodexPath => Path.Combine(MockCliDir, "mock_codex.py");
    public string MockClaudePath => Path.Combine(MockCliDir, "mock_claude.py");
    public string MockAcpPath => Path.Combine(MockCliDir, "mock_acp.py");
    public string MockBwrapPath => Path.Combine(MockCliDir, "mock_bwrap.py");
    public string BwrapCapturedArgsPath => Path.Combine(RootDir, "bwrap_captured_args.json");
    public string MockSlowVersionPath => Path.Combine(MockCliDir, "mock_slow.py");
    public string MockFailingVersionPath => Path.Combine(MockCliDir, "mock_fail.py");

    public Dictionary<string, ProviderCommandOverride> UnavailableProviders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex"] = new(Path.Combine(RootDir, "unavailable-codex")),
        ["claude"] = new(Path.Combine(RootDir, "unavailable-claude")),
        ["acp:antigravity"] = new(Path.Combine(RootDir, "unavailable-agy")),
        ["acp:opencode"] = new(Path.Combine(RootDir, "unavailable-opencode"))
    };

    public MockCliFixture()
    {
        RootDir = Path.Combine(Path.GetTempPath(), $"perga-agentkit-tests-{Guid.NewGuid():N}");
        WorkspaceDir = Path.Combine(RootDir, "workspace");
        Directory.CreateDirectory(WorkspaceDir);
    }

    public static IReadOnlyDictionary<string, McpServerConfig> CreateSampleMcpServers() =>
        new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["perga"] = new McpServerConfig.HttpMcpServer(
                "http://127.0.0.1:43111/mcp/kernel",
                new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer pg_test_token_123"
                }),
            ["local-tools"] = new McpServerConfig.StdioMcpServer(
                "/usr/bin/env",
                ["python3", "-m", "mcp_local"],
                new Dictionary<string, string>
                {
                    ["MCP_MODE"] = "test"
                })
        };

    public string WriteExecutable(string fileName, string content)
    {
        var fullPath = Path.Combine(RootDir, fileName);
        File.WriteAllText(fullPath, content.Replace("\r\n", "\n", StringComparison.Ordinal), Utf8NoBom);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDir))
            {
                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch
        {
            // Ignore best-effort temp directory cleanup failure.
        }
    }
}
