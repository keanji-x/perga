using Perga.Contracts;

namespace Perga.Kernel.Settings;

public sealed record AppSettingsSnapshot(
    SettingsSnapshotDto Contract,
    string DefaultProvider,
    double AutoCompactionThreshold,
    TimeSpan SessionIdleTimeout);
