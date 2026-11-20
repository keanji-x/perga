using System.Text.Json;

namespace Perga.AgentKit;

internal abstract record AcpPermissionTransport
{
    private AcpPermissionTransport() { }
    internal sealed record RpcConfigOption(string ConfigId) : AcpPermissionTransport;
    internal sealed record RpcLegacyMode : AcpPermissionTransport;
    internal sealed record LaunchArgument : AcpPermissionTransport;
}

internal sealed record AcpPermissionEntry(
    PermissionModeOption Option,
    AcpPermissionTransport Transport);

/// <summary>
/// Immutable snapshot of an ACP session's available permission modes and how each mode is applied.
/// Combines protocol-advertised modes with provider-declared launch-argument modes without drift.
/// </summary>
internal sealed class AcpPermissionCatalog
{
    public static AcpPermissionCatalog Empty { get; } = new([], []);

    private readonly IReadOnlyList<LaunchPermissionMode> _launchModes;
    private readonly Dictionary<string, AcpPermissionEntry> _entriesById;

    private AcpPermissionCatalog(
        IReadOnlyList<AcpPermissionEntry> entries,
        IReadOnlyList<LaunchPermissionMode> launchModes)
    {
        _launchModes = launchModes;
        _entriesById = entries.ToDictionary(entry => entry.Option.Id, StringComparer.Ordinal);
        Options = Array.AsReadOnly(entries.Select(entry => entry.Option).ToArray());
    }

    public IReadOnlyList<PermissionModeOption> Options { get; }

    public static AcpPermissionCatalog Create(IReadOnlyList<LaunchPermissionMode>? launchModes)
    {
        var registered = launchModes is { Count: > 0 }
            ? Array.AsReadOnly(launchModes.ToArray())
            : (IReadOnlyList<LaunchPermissionMode>)[];
        if (registered.Count == 0)
        {
            return Empty;
        }

        var entries = registered
            .Select(mode => new AcpPermissionEntry(mode.Option, new AcpPermissionTransport.LaunchArgument()))
            .ToArray();
        return new AcpPermissionCatalog(entries, registered);
    }

    public bool TryResolve(string modeId, out AcpPermissionEntry entry) =>
        _entriesById.TryGetValue(modeId, out entry!);

    public bool IsLaunchArgument(string? modeId) =>
        !string.IsNullOrWhiteSpace(modeId) &&
        _entriesById.TryGetValue(modeId, out var entry) &&
        entry.Transport is AcpPermissionTransport.LaunchArgument;

    public AcpPermissionCatalog RefreshFromProtocol(JsonElement response)
    {
        // Empty success responses do not advertise a replacement catalogue.
        if (response.ValueKind != JsonValueKind.Object ||
            (!response.TryGetProperty("configOptions", out _) && !response.TryGetProperty("modes", out _)))
        {
            return this;
        }

        var (protocolOptions, protocolTransport) = ReadProtocolModes(response);
        var launchById = _launchModes.ToDictionary(mode => mode.Option.Id, StringComparer.Ordinal);
        var merged = new List<AcpPermissionEntry>(protocolOptions.Count + _launchModes.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var option in protocolOptions)
        {
            if (!seen.Add(option.Id))
            {
                continue;
            }

            if (launchById.TryGetValue(option.Id, out var launchMode))
            {
                merged.Add(new AcpPermissionEntry(launchMode.Option, new AcpPermissionTransport.LaunchArgument()));
            }
            else if (protocolTransport is not null)
            {
                merged.Add(new AcpPermissionEntry(option, protocolTransport));
            }
        }

        foreach (var launchMode in _launchModes)
        {
            if (seen.Add(launchMode.Option.Id))
            {
                merged.Add(new AcpPermissionEntry(launchMode.Option, new AcpPermissionTransport.LaunchArgument()));
            }
        }

        return new AcpPermissionCatalog(merged, _launchModes);
    }

    private static (IReadOnlyList<PermissionModeOption> Options, AcpPermissionTransport? Transport) ReadProtocolModes(JsonElement response)
    {
        if (response.TryGetProperty("configOptions", out var configs) && configs.ValueKind == JsonValueKind.Array)
        {
            foreach (var config in configs.EnumerateArray())
            {
                var id = Text(config, "id");
                if ((Text(config, "category") != "mode" && id != "mode") ||
                    Text(config, "type") != "select" || string.IsNullOrWhiteSpace(id) ||
                    !config.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                return (ParseOptions(options, Text(config, "currentValue"), configBacked: true),
                    new AcpPermissionTransport.RpcConfigOption(id));
            }
        }

        if (response.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Object &&
            modes.TryGetProperty("availableModes", out var available) && available.ValueKind == JsonValueKind.Array)
        {
            return (ParseOptions(available, Text(modes, "currentModeId"), configBacked: false),
                new AcpPermissionTransport.RpcLegacyMode());
        }

        return ([], null);
    }

    private static IReadOnlyList<PermissionModeOption> ParseOptions(JsonElement options, string? current, bool configBacked)
    {
        var result = new List<PermissionModeOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonElement values)
        {
            foreach (var option in values.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object) continue;
                if (option.TryGetProperty("options", out var children) && children.ValueKind == JsonValueKind.Array)
                    Visit(children);
                var id = Text(option, configBacked ? "value" : "id");
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    result.Add(new(id, Text(option, "name") ?? id, Text(option, "description") ?? "", "neutral", id == current));
            }
        }
        Visit(options);
        return result;
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}
