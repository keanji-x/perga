using System.Text.Json;

namespace Perga.AgentKit;

public sealed partial class AcpAgentSession
{
    private ConfigurationSupport _permissionChangeSupport = ConfigurationSupport.Unknown;
    private AcpPermissionCatalog _permissionCatalog = AcpPermissionCatalog.Empty;

    private void RefreshPermissionModes(JsonElement response)
    {
        var current = Volatile.Read(ref _permissionCatalog);
        var updated = current.RefreshFromProtocol(response);
        if (!ReferenceEquals(updated, current))
        {
            Volatile.Write(ref _permissionCatalog, updated);
        }
    }

    private static AcpPermissionEntry? ValidatePermissionMode(string? mode, AcpPermissionCatalog catalog)
    {
        if (mode is null)
        {
            return null;
        }

        if (!catalog.TryResolve(mode, out var entry))
        {
            throw new ArgumentException($"Unsupported ACP permission mode '{mode}'.", nameof(mode));
        }

        return entry;
    }

    private Task<JsonElement> SetPermissionModeAsync(
        string mode, string sessionId, AcpPermissionTransport transport, CancellationToken ct)
    {
        var parameters = new Dictionary<string, object?> { ["sessionId"] = sessionId };
        string method;
        switch (transport)
        {
            case AcpPermissionTransport.RpcConfigOption config:
                method = "session/set_config_option";
                parameters["configId"] = config.ConfigId;
                parameters["value"] = mode;
                break;
            case AcpPermissionTransport.RpcLegacyMode:
                method = "session/set_mode";
                parameters["modeId"] = mode;
                break;
            default:
                throw new InvalidOperationException($"ACP permission mode '{mode}' cannot be applied via RPC.");
        }

        return _rpc.SendRequestAsync(method, parameters, ct, response =>
        {
            RefreshPermissionModes(response);
            if (IsPositiveAcknowledgement(response)) _permissionChangeSupport = ConfigurationSupport.Supported;
        });
    }
}
