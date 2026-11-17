using System.Text.Json;

namespace Perga.Storage.Sqlite;

/// <summary>
/// Configuration options for <see cref="SqliteTrackStore"/> and <see cref="SqliteSingleWriter"/>.
/// </summary>
public sealed record SqliteStorageOptions
{
    public const int DefaultWriteChannelCapacity = 4096;
    public const int DefaultMaxBatchSize = 32;
    public const int DefaultBusyTimeoutSeconds = 30;
    public const int DefaultColdBlobThresholdBytes = 65_536; // 64 KB
    public const int DefaultColdBlobPreviewBytes = 2_048;    // 2 KB
    public const int DefaultFreePageVacuumThreshold = 4;

    public required string DbPath { get; init; }
    public string? DataDir { get; init; }
    public int WriteChannelCapacity { get; init; } = DefaultWriteChannelCapacity;
    public int MaxBatchSize { get; init; } = DefaultMaxBatchSize;
    public int BusyTimeoutSeconds { get; init; } = DefaultBusyTimeoutSeconds;
    public int ColdBlobThresholdBytes { get; init; } = DefaultColdBlobThresholdBytes;
    public int ColdBlobPreviewBytes { get; init; } = DefaultColdBlobPreviewBytes;
    public int FreePageVacuumThreshold { get; init; } = DefaultFreePageVacuumThreshold;

    public string GetFullDbPath() => Path.GetFullPath(DbPath);

    public string ResolveDataDir()
    {
        if (!string.IsNullOrWhiteSpace(DataDir))
        {
            return Path.GetFullPath(DataDir);
        }

        var fullDbPath = GetFullDbPath();
        return Path.GetDirectoryName(fullDbPath) ?? Directory.GetCurrentDirectory();
    }

    public string ResolveBlobsRootDir() => Path.Combine(ResolveDataDir(), "blobs");
}

/// <summary>
/// Captures an in-flight <c>task_attempts</c> workspace lease discovered during boot crash reconciliation.
/// </summary>
public sealed record InterruptedAttemptLeaseDto(
    string AttemptId,
    string TaskId,
    string TrackId,
    JsonElement WorkspaceLease);
