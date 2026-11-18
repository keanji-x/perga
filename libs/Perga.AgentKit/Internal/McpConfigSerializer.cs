using System.Text.Json;

namespace Perga.AgentKit.Internal;

internal static class McpConfigSerializer
{
    public static Dictionary<string, object?> BuildCodexMcpServersMap(
        IReadOnlyDictionary<string, McpServerConfig>? servers)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (servers is null)
        {
            return map;
        }

        foreach (var (name, server) in servers)
        {
            switch (server)
            {
                case McpServerConfig.HttpMcpServer http:
                    map[name] = new Dictionary<string, object?>
                    {
                        ["type"] = "http",
                        ["url"] = http.Url,
                        ["headers"] = http.Headers,
                        ["http_headers"] = http.Headers
                    };
                    break;

                case McpServerConfig.StdioMcpServer stdio:
                    map[name] = new Dictionary<string, object?>
                    {
                        ["type"] = "stdio",
                        ["command"] = stdio.Command,
                        ["args"] = stdio.Args,
                        ["env"] = stdio.Env
                    };
                    break;
            }
        }

        return map;
    }

    public static string SerializeClaudeMcpConfigJson(
        IReadOnlyDictionary<string, McpServerConfig>? servers)
    {
        var mcpServers = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (servers is not null)
        {
            foreach (var (name, server) in servers)
            {
                switch (server)
                {
                    case McpServerConfig.HttpMcpServer http:
                        mcpServers[name] = new Dictionary<string, object?>
                        {
                            ["type"] = "http",
                            ["url"] = http.Url,
                            ["headers"] = http.Headers
                        };
                        break;

                    case McpServerConfig.StdioMcpServer stdio:
                        mcpServers[name] = new Dictionary<string, object?>
                        {
                            ["type"] = "stdio",
                            ["command"] = stdio.Command,
                            ["args"] = stdio.Args,
                            ["env"] = stdio.Env
                        };
                        break;
                }
            }
        }

        var root = new Dictionary<string, object?>
        {
            ["mcpServers"] = mcpServers
        };

        return JsonSerializer.Serialize(root);
    }

    public static List<Dictionary<string, object?>> BuildAcpMcpServersList(
        IReadOnlyDictionary<string, McpServerConfig>? servers,
        bool supportsHttpMcp = true,
        string? nodeExecutablePath = null)
    {
        var list = new List<Dictionary<string, object?>>();
        if (servers is null)
        {
            return list;
        }

        foreach (var (name, server) in servers)
        {
            switch (server)
            {
                case McpServerConfig.HttpMcpServer http when supportsHttpMcp:
                {
                    var headers = new List<Dictionary<string, string>>(http.Headers.Count);
                    foreach (var kvp in http.Headers)
                    {
                        headers.Add(new Dictionary<string, string>
                        {
                            ["name"] = kvp.Key,
                            ["value"] = kvp.Value
                        });
                    }

                    list.Add(new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["type"] = "http",
                        ["url"] = http.Url,
                        ["headers"] = headers
                    });
                    break;
                }

                case McpServerConfig.HttpMcpServer http:
                {
                    var headersJson = JsonSerializer.Serialize(http.Headers);
                    var resolvedNode = string.IsNullOrWhiteSpace(nodeExecutablePath)
                        ? "node"
                        : nodeExecutablePath;
                    var env = new List<Dictionary<string, string>>(4)
                    {
                        new() { ["name"] = "PERGA_MCP_URL", ["value"] = http.Url },
                        new() { ["name"] = "PERGA_MCP_HEADERS", ["value"] = headersJson },
                        new() { ["name"] = "NO_PROXY", ["value"] = "127.0.0.1,localhost" },
                        new() { ["name"] = "no_proxy", ["value"] = "127.0.0.1,localhost" }
                    };

                    list.Add(new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["command"] = resolvedNode,
                        ["args"] = new[] { "-e", AcpStdioHttpMcpBridge.BridgeScript },
                        ["env"] = env
                    });
                    break;
                }

                case McpServerConfig.StdioMcpServer stdio:
                {
                    var env = new List<Dictionary<string, string>>(stdio.Env.Count);
                    foreach (var kvp in stdio.Env)
                    {
                        env.Add(new Dictionary<string, string>
                        {
                            ["name"] = kvp.Key,
                            ["value"] = kvp.Value
                        });
                    }

                    list.Add(new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["command"] = stdio.Command,
                        ["args"] = stdio.Args,
                        ["env"] = env
                    });
                    break;
                }
            }
        }

        return list;
    }
}
