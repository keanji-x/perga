using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Perga.AgentKit;
using Perga.Host.Configuration;
using Perga.Kernel.Ports;

namespace Perga.Host.Mcp;

public sealed class HostMcpSessionBridge : IMcpSessionBridge
{
    private readonly PergaHostOptions _options;
    private readonly IServer? _server;
    private readonly Func<McpPluginRouter?> _getPluginRouter;
    private readonly ConcurrentDictionary<string, McpSessionPrincipal> _byToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokenBySession = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public HostMcpSessionBridge(PergaHostOptions options, IServer? server = null, McpPluginRouter? pluginRouter = null)
        : this(options, server, () => pluginRouter)
    {
    }

    internal HostMcpSessionBridge(PergaHostOptions options, IServer? server, Func<McpPluginRouter?> getPluginRouter)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _server = server;
        _getPluginRouter = getPluginRouter;
    }

    public IReadOnlyCollection<string>? ConfiguredPluginIds => _getPluginRouter()?.RegisteredPluginIds;

    public McpSessionRegistration RegisterSession(
        string trackId,
        string sessionId,
        bool isLead,
        string? boundTaskId,
        int? taskRunCount,
        IReadOnlyList<string> enabledPlugins,
        string? runtimeBindingId = null,
        string? baseUriOverride = null)
    {
        McpSessionPrincipal principal;
        lock (_gate)
        {
            if (_tokenBySession.TryRemove(sessionId, out var oldToken))
            {
                _byToken.TryRemove(oldToken, out _);
            }

            var token = $"pg_{Guid.NewGuid():N}";
            principal = new McpSessionPrincipal(
                token,
                trackId,
                sessionId,
                isLead,
                boundTaskId,
                taskRunCount,
                enabledPlugins.ToArray()) { RuntimeBindingId = runtimeBindingId };

            _byToken[token] = principal;
            _tokenBySession[sessionId] = token;
        }

        return new McpSessionRegistration(principal, BuildMcpServerMap(principal, baseUriOverride));
    }

    public McpSessionPrincipal? ResolveToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var normalized = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? token["Bearer ".Length..].Trim()
            : token.Trim();

        if (!normalized.StartsWith("pg_", StringComparison.Ordinal))
        {
            return null;
        }

        return _byToken.TryGetValue(normalized, out var principal) ? principal : null;
    }

    public string? GetTokenForSession(string sessionId) =>
        _tokenBySession.TryGetValue(sessionId, out var token) ? token : null;

    public void UpdateLeadStatus(string trackId, string? newLeadSessionId)
    {
        lock (_gate)
        {
            foreach (var kvp in _byToken.ToArray())
            {
                if (string.Equals(kvp.Value.TrackId, trackId, StringComparison.Ordinal))
                {
                    var isLead = string.Equals(kvp.Value.SessionId, newLeadSessionId, StringComparison.Ordinal);
                    if (kvp.Value.IsLead != isLead)
                    {
                        _byToken[kvp.Key] = kvp.Value with { IsLead = isLead };
                    }
                }
            }
        }
    }

    public void UpdateTrackPlugins(string trackId, IReadOnlyList<string> enabledPlugins)
    {
        var pluginsCopy = enabledPlugins.ToArray();
        lock (_gate)
        {
            foreach (var kvp in _byToken.ToArray())
            {
                if (string.Equals(kvp.Value.TrackId, trackId, StringComparison.Ordinal))
                {
                    _byToken[kvp.Key] = kvp.Value with { EnabledPlugins = pluginsCopy };
                }
            }
        }
    }

    public void RevokeSession(string sessionId, string? runtimeBindingId = null)
    {
        lock (_gate)
        {
            if (!_tokenBySession.TryGetValue(sessionId, out var token) ||
                runtimeBindingId is not null && _byToken[token].RuntimeBindingId != runtimeBindingId) return;
            _tokenBySession.TryRemove(sessionId, out _);
            _byToken.TryRemove(token, out _);
        }
    }

    public void RevokeTrack(string trackId)
    {
        lock (_gate)
        {
            foreach (var kvp in _byToken.ToArray())
            {
                if (string.Equals(kvp.Value.TrackId, trackId, StringComparison.Ordinal))
                {
                    _byToken.TryRemove(kvp.Key, out _);
                    _tokenBySession.TryRemove(kvp.Value.SessionId, out _);
                }
            }
        }
    }

    public string ResolveBaseUri()
    {
        if (!string.IsNullOrWhiteSpace(_options.McpBaseUri))
        {
            return _options.McpBaseUri.TrimEnd('/');
        }

        var addresses = _server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is not null)
        {
            foreach (var addr in addresses)
            {
                if (!string.IsNullOrWhiteSpace(addr))
                {
                    return NormalizeLoopbackAddress(addr).TrimEnd('/');
                }
            }
        }

        var urlsEnv = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(urlsEnv))
        {
            foreach (var candidate in urlsEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!candidate.EndsWith(":0", StringComparison.Ordinal))
                {
                    return NormalizeLoopbackAddress(candidate).TrimEnd('/');
                }
            }
        }

        return "http://127.0.0.1:5000";
    }

    private IReadOnlyDictionary<string, McpServerConfig> BuildMcpServerMap(McpSessionPrincipal principal, string? baseUriOverride = null)
    {
        var baseUri = string.IsNullOrWhiteSpace(baseUriOverride)
            ? ResolveBaseUri()
            : baseUriOverride.TrimEnd('/');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {principal.Token}"
        };

        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["perga"] = new McpServerConfig.HttpMcpServer($"{baseUri}/mcp/kernel", headers),
            ["vcs"] = new McpServerConfig.HttpMcpServer($"{baseUri}/mcp/vcs", headers),
            [MemoryTrackBinding.McpServerName] = new McpServerConfig.HttpMcpServer($"{baseUri}/mcp/memory", headers)
        };

        foreach (var pluginId in principal.EnabledPlugins)
        {
            if (string.IsNullOrWhiteSpace(pluginId) ||
                string.Equals(pluginId, "perga", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pluginId, "vcs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pluginId, MemoryTrackBinding.McpServerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            servers[pluginId] = new McpServerConfig.HttpMcpServer(
                $"{baseUri}/mcp/p/{Uri.EscapeDataString(pluginId)}",
                headers);
        }

        return servers;
    }

    private static string NormalizeLoopbackAddress(string rawAddress) =>
        rawAddress
            .Replace("://0.0.0.0:", "://127.0.0.1:", StringComparison.Ordinal)
            .Replace("://[::]:", "://127.0.0.1:", StringComparison.Ordinal)
            .Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal)
            .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal);
}
