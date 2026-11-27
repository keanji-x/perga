using Perga.Contracts;

namespace Perga.Kernel.Settings;

public sealed class AppSettingsService(AppSettingsRegistry registry, IAppSettingsStore store) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Use a fresh revision range on restart so a client cannot reuse a previous Host's
    // revision. Leave room to increment while keeping revisions safe as JS integers.
    private AppSettingsSnapshot _current = registry.Project(Random.Shared.NextInt64(1, 1L << 48),
        new Dictionary<string, object>());
    private Dictionary<string, object> _overrides = new(StringComparer.Ordinal);

    public AppSettingsSnapshot Current => Volatile.Read(ref _current);

    public async Task<AppSettingsSnapshot> ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReloadCoreAsync(ct).ConfigureAwait(false);
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettingsSnapshot> PatchAsync(PatchSettingsRequestDto request, CancellationToken ct = default)
    {
        if (request.Patch is null)
            throw new AppSettingValidationException("patch", "Expected an object of setting values.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReloadCoreAsync(ct).ConfigureAwait(false);
            if (request.ExpectedRev != Current.Contract.Rev)
                throw KernelException.Conflict("Settings changed since this revision. Reload settings and retry.");

            if (request.Patch.Count == 0) return Current;

            var updated = new Dictionary<string, object>(_overrides, StringComparer.Ordinal);
            foreach (var (key, value) in request.Patch)
                updated[key] = registry.Validate(key, value);

            var snapshot = registry.Project(Current.Contract.Rev + 1, updated);
            await store.WriteAsync(updated, ct).ConfigureAwait(false);
            Publish(updated, snapshot);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReloadCoreAsync(CancellationToken ct)
    {
        try
        {
            var read = await store.ReadAsync(ct).ConfigureAwait(false);
            var loaded = read.ToDictionary(pair => pair.Key, pair => registry.Validate(pair.Key, pair.Value), StringComparer.Ordinal);
            if (loaded.Count != _overrides.Count ||
                loaded.Any(pair => !_overrides.TryGetValue(pair.Key, out var value) || !Equals(value, pair.Value)))
            {
                Publish(loaded, registry.Project(Current.Contract.Rev + 1, loaded));
            }
        }
        catch (AppSettingValidationException ex)
        {
            throw new AppSettingsLoadException($"Invalid settings.toml value for '{ex.Field}'. {ex.Message}", ex);
        }
        catch (FormatException ex)
        {
            throw new AppSettingsLoadException("Could not load settings.toml. " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AppSettingsLoadException("Could not read settings.toml. Check the Host logs for details.", ex);
        }
    }

    private void Publish(Dictionary<string, object> overrides, AppSettingsSnapshot snapshot)
    {
        _overrides = overrides;
        Volatile.Write(ref _current, snapshot);
    }

    public void Dispose() => _gate.Dispose();
}

public sealed class AppSettingsLoadException(string message, Exception innerException) : Exception(message, innerException);
