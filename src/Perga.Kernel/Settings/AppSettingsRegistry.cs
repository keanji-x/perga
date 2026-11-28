using System.Collections.Frozen;
using System.Text.Json;
using Perga.Contracts;

namespace Perga.Kernel.Settings;

public sealed class AppSettingsRegistry
{
    private const string DefaultProvider = "planners.default_provider";
    private const string AutoCompaction = "sessions.auto_compaction_percent";
    private const string IdleTimeout = "sessions.idle_timeout_minutes";
    private const string Planners = "planners";

    private static readonly IReadOnlyList<SettingsSectionDto> Sections = Array.AsReadOnly(
        new[] { new SettingsSectionDto(Planners, "Planners", null) });

    private readonly KernelOptions _defaults;
    private readonly FrozenDictionary<string, SettingsFieldDto> _fieldsByKey;
    private readonly FrozenSet<string> _providers;
    private readonly IReadOnlyList<SettingsFieldDto> _fields;

    public AppSettingsRegistry(KernelOptions defaults, IEnumerable<string> registeredProviders)
    {
        _defaults = defaults;
        var providerOptions = registeredProviders.Select(SessionLaunchValidator.NormalizeProvider)
            .Distinct(StringComparer.Ordinal)
            .Select(id => new SettingsFieldOptionDto(id, id))
            .ToArray();
        _providers = providerOptions.Select(option => option.Value).ToFrozenSet(StringComparer.Ordinal);

        _fields = Array.AsReadOnly<SettingsFieldDto>(
        [
            new(DefaultProvider, Planners, "Default provider", "Used when creating a Track without a selected provider.",
                "select", "string", JsonSerializer.SerializeToElement(defaults.DefaultProvider), null, null, null,
                Array.AsReadOnly(providerOptions)),
            new(AutoCompaction, Planners, "Automatic compaction", "Compact a session after a turn when context usage reaches this threshold.",
                "segmented", "integer", JsonSerializer.SerializeToElement((int)Math.Round(defaults.AutoCompactionThreshold * 100)), 50, 95, "%",
                Array.AsReadOnly<SettingsFieldOptionDto>([new("70", "70%"), new("80", "80%"), new("90", "90%")])),
            new(IdleTimeout, Planners, "Session idle timeout", "Close idle session runtimes after this interval.",
                "select", "integer", JsonSerializer.SerializeToElement((int)Math.Round(defaults.SessionIdleTimeout.TotalMinutes)), 5, 240, "min",
                Array.AsReadOnly<SettingsFieldOptionDto>(
                    [new("5", "5 min"), new("15", "15 min"), new("30", "30 min"), new("60", "1 hr"), new("120", "2 hr")]))
        ]);
        _fieldsByKey = _fields.ToFrozenDictionary(field => field.Key, StringComparer.Ordinal);
    }

    internal object Validate(string key, object? value)
    {
        if (!_fieldsByKey.TryGetValue(key, out var field))
            throw new AppSettingValidationException(key, "Unknown setting.");

        if (key == DefaultProvider)
        {
            if (value is not string provider || !_providers.Contains(provider))
                throw new AppSettingValidationException(key, "Expected a canonical registered provider ID.");

            return provider;
        }

        if (field.ValueType == "integer")
        {
            if (value is not long number || number < field.Min || number > field.Max)
                throw new AppSettingValidationException(key, $"Expected an integer from {field.Min} through {field.Max}.");

            return number;
        }

        throw new AppSettingValidationException(key, "Unsupported setting type.");
    }

    internal object Validate(string key, JsonElement value) => Validate(key, value.ValueKind switch
    {
        JsonValueKind.String => ReadString(key, value),
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        _ => null
    });

    private static string? ReadString(string key, JsonElement value)
    {
        try { return value.GetString(); }
        catch (InvalidOperationException)
        {
            throw new AppSettingValidationException(key, "Expected a well-formed JSON string.");
        }
    }

    internal AppSettingsSnapshot Project(long rev, IReadOnlyDictionary<string, object> overrides)
    {
        var values = _fields.ToFrozenDictionary(field => field.Key,
            field => overrides.TryGetValue(field.Key, out var value) ? JsonSerializer.SerializeToElement(value) : field.DefaultValue,
            StringComparer.Ordinal);
        return new(
            new SettingsSnapshotDto(rev, Sections, _fields, values),
            overrides.TryGetValue(DefaultProvider, out var provider) ? (string)provider : _defaults.DefaultProvider,
            overrides.TryGetValue(AutoCompaction, out var percent) ? (long)percent / 100d : _defaults.AutoCompactionThreshold,
            overrides.TryGetValue(IdleTimeout, out var minutes) ? TimeSpan.FromMinutes((long)minutes) : _defaults.SessionIdleTimeout);
    }
}

public sealed class AppSettingValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}
