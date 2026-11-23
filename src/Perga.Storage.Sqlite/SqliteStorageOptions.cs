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
    public const long DefaultMaxAttachmentBytes = 10_485_760L; // 10 MiB
    public const int DefaultMaxAttachmentsPerMessage = 8;
    public const int MaxSchemaAttachmentsPerMessage = 8;
    public const long DefaultMaxSessionAttachmentBytes = 67_108_864L; // 64 MiB
    public const long DefaultMaxUnownedStagedAttachmentBytes = 67_108_864L; // 64 MiB
    public const int DefaultMaxUnownedStagedAttachmentCount = 64;
    public const long DefaultStagedAttachmentTtlMs = 86_400_000L; // 24 hours
    public const long DefaultOrphanAttachmentGraceMs = 600_000L;  // 10 minutes

    public required string DbPath { get; init; }
    public string? DataDir { get; init; }
    public int WriteChannelCapacity { get; init; } = DefaultWriteChannelCapacity;
    public int MaxBatchSize { get; init; } = DefaultMaxBatchSize;
    public int BusyTimeoutSeconds { get; init; } = DefaultBusyTimeoutSeconds;
    public int ColdBlobThresholdBytes { get; init; } = DefaultColdBlobThresholdBytes;
    public int ColdBlobPreviewBytes { get; init; } = DefaultColdBlobPreviewBytes;
    public int FreePageVacuumThreshold { get; init; } = DefaultFreePageVacuumThreshold;
    public long MaxAttachmentBytes { get; init; } = DefaultMaxAttachmentBytes;
    public int MaxAttachmentsPerMessage { get; init; } = DefaultMaxAttachmentsPerMessage;
    public long MaxSessionAttachmentBytes { get; init; } = DefaultMaxSessionAttachmentBytes;
    public long MaxUnownedStagedAttachmentBytes { get; init; } = DefaultMaxUnownedStagedAttachmentBytes;
    public int MaxUnownedStagedAttachmentCount { get; init; } = DefaultMaxUnownedStagedAttachmentCount;
    public long StagedAttachmentTtlMs { get; init; } = DefaultStagedAttachmentTtlMs;
    public long OrphanAttachmentGraceMs { get; init; } = DefaultOrphanAttachmentGraceMs;

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
    public string ResolveAttachmentsRootDir() => Path.Combine(ResolveDataDir(), "attachments");
}
