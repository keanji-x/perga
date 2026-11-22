using System.Collections.Concurrent;
using Perga.AgentKit;

namespace Perga.Kernel.Ports;

public sealed record McpSessionPrincipal(
    string Token,
    string TrackId,
    string SessionId,
    bool IsLead,
    string? BoundTaskId,
    int? TaskRunCount,
    IReadOnlyList<string> EnabledPlugins)
{
    public string? RuntimeBindingId { get; init; }
}

public sealed record McpSessionRegistration(
    McpSessionPrincipal Principal,
    IReadOnlyDictionary<string, McpServerConfig> McpServers);

public interface IMcpSessionBridge
{
    McpSessionRegistration RegisterSession(
        string trackId,
        string sessionId,
        bool isLead,
        string? boundTaskId,
        int? taskRunCount,
        IReadOnlyList<string> enabledPlugins,
        string? runtimeBindingId = null,
        string? baseUriOverride = null);

    string ResolveBaseUri() => "http://127.0.0.1:5000";

    IReadOnlyCollection<string>? ConfiguredPluginIds => null;

    McpSessionPrincipal? ResolveToken(string token);
    string? GetTokenForSession(string sessionId);
    void UpdateLeadStatus(string trackId, string? newLeadSessionId);
    void UpdateTrackPlugins(string trackId, IReadOnlyList<string> enabledPlugins);
    void RevokeSession(string sessionId, string? runtimeBindingId = null);
    void RevokeTrack(string trackId);
}

public sealed class InMemoryMcpSessionBridge : IMcpSessionBridge
{
    private readonly string _baseUri;
    private readonly ConcurrentDictionary<string, McpSessionPrincipal> _byToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokenBySession = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public InMemoryMcpSessionBridge(string baseUri = "http://127.0.0.1:5000")
    {
        _baseUri = baseUri.TrimEnd('/');
    }

    public string ResolveBaseUri() => _baseUri;

    public IReadOnlyCollection<string>? ConfiguredPluginIds { get; init; }

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

    private IReadOnlyDictionary<string, McpServerConfig> BuildMcpServerMap(McpSessionPrincipal principal, string? baseUriOverride = null)
    {
        var baseUri = string.IsNullOrWhiteSpace(baseUriOverride) ? _baseUri : baseUriOverride.TrimEnd('/');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {principal.Token}"
        };

        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["perga"] = new McpServerConfig.HttpMcpServer($"{baseUri}/mcp/kernel", headers),
            ["vcs"] = new McpServerConfig.HttpMcpServer($"{baseUri}/mcp/vcs", headers)
        };

        foreach (var pluginId in principal.EnabledPlugins)
        {
            if (string.IsNullOrWhiteSpace(pluginId) ||
                string.Equals(pluginId, "perga", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pluginId, "vcs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            servers[pluginId] = new McpServerConfig.HttpMcpServer(
                $"{baseUri}/mcp/p/{Uri.EscapeDataString(pluginId)}",
                headers);
        }

        return servers;
    }
}
