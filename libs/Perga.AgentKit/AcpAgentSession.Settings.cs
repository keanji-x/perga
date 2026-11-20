using System.Text.Json;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public sealed partial class AcpAgentSession
{
    private ConfigurationSupport _permissionChangeSupport = ConfigurationSupport.Unknown;
    private AcpPermissionModes _permissionModes = new([], null);
    private IReadOnlyList<PermissionModeOption> _extraPermissionModes = [];
    private IReadOnlySet<string> _launchArgumentModes = new HashSet<string>(StringComparer.Ordinal);

    private void RefreshPermissionModes(JsonElement response)
    {
        // Empty success responses do not advertise a replacement catalogue.
        if (response.ValueKind != JsonValueKind.Object ||
            (!response.TryGetProperty("configOptions", out _) && !response.TryGetProperty("modes", out _)))
            return;

        var modes = AcpPermissionModes.Read(response);
        var merged = modes.Options.ToList();
        foreach (var extra in _extraPermissionModes)
            if (!merged.Any(option => string.Equals(option.Id, extra.Id, StringComparison.Ordinal)))
                merged.Add(extra);

        // Publish options and their transport together; readers retain one immutable snapshot.
        Volatile.Write(ref _permissionModes, new(Array.AsReadOnly(merged.ToArray()), modes.ConfigId));
    }

    private static void ValidatePermissionMode(string? mode, AcpPermissionModes modes)
    {
        if (mode is not null && !modes.Options.Any(option => option.Id == mode))
            throw new ArgumentException($"Unsupported ACP permission mode '{mode}'.", nameof(mode));
    }

    private Task<JsonElement> SetPermissionModeAsync(string mode, string sessionId, AcpPermissionModes modes, CancellationToken ct)
    {
        var parameters = new Dictionary<string, object?> { ["sessionId"] = sessionId };
        if (modes.ConfigId is { } configId)
        {
            parameters["configId"] = configId;
            parameters["value"] = mode;
        }
        else parameters["modeId"] = mode;
        return _rpc.SendRequestAsync(modes.ConfigId is null ? "session/set_mode" : "session/set_config_option", parameters, ct, response =>
        {
            RefreshPermissionModes(response);
            if (IsPositiveAcknowledgement(response)) _permissionChangeSupport = ConfigurationSupport.Supported;
        });
    }

}
