namespace Perga.Kernel.Settings;

/// <summary>Stores explicit overrides. An absent file reads as an empty set.</summary>
public interface IAppSettingsStore
{
    Task<IReadOnlyDictionary<string, object>> ReadAsync(CancellationToken ct);
    Task WriteAsync(IReadOnlyDictionary<string, object> values, CancellationToken ct);
}
