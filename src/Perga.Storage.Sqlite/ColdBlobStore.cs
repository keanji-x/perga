using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace Perga.Storage.Sqlite;

/// <summary>
/// Manages external cold Zstd blob offloading (<c>&gt; 64 KB</c>) into <c>{dataDir}/blobs/{trackId}/{sha256}.zst</c>,
/// <c>"v": 1</c> envelope generation, transparent read hydration, and O(1) track blob cleanup.
/// </summary>
internal sealed class ColdBlobStore
{
    public const int TombstonePreviewBytes = 512;

    private readonly string _dataDir;
    private readonly string _blobsRootDir;
    private readonly string _blobsRootPrefixWithSep;
    private readonly int _thresholdBytes;
    private readonly int _previewBytes;
    private readonly SqliteJsonUpcaster _upcaster;

    public ColdBlobStore(SqliteStorageOptions options, SqliteJsonUpcaster? upcaster = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataDir = options.ResolveDataDir();
        _blobsRootDir = Path.GetFullPath(options.ResolveBlobsRootDir());
        _blobsRootPrefixWithSep = _blobsRootDir.EndsWith(Path.DirectorySeparatorChar)
            ? _blobsRootDir
            : _blobsRootDir + Path.DirectorySeparatorChar;
        _thresholdBytes = Math.Max(1, options.ColdBlobThresholdBytes);
        _previewBytes = Math.Max(64, options.ColdBlobPreviewBytes);
        _upcaster = upcaster ?? SqliteJsonUpcaster.Default;
    }

    public string BlobsRootDir => _blobsRootDir;

    public string StoreOrInline(string trackId, JsonElement payload)
    {
        var rawJson = SqliteJsonUpcaster.SerializeElement(payload);
        var rawBytes = Encoding.UTF8.GetBytes(rawJson);

        if (rawBytes.Length <= _thresholdBytes)
        {
            return rawJson;
        }

        return WriteColdBlobAndBuildEnvelope(trackId, rawJson, rawBytes);
    }

    public string? StoreOrInlineNullable(string trackId, JsonElement? payload)
    {
        if (payload is not { ValueKind: not JsonValueKind.Undefined } value)
        {
            return null;
        }

        return StoreOrInline(trackId, value);
    }

    private string WriteColdBlobAndBuildEnvelope(string trackId, string rawJson, byte[] rawBytes)
    {
        var safeTrackDir = ValidateTrackDirectorySegment(trackId);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(rawBytes));
        var blobRef = $"blobs/{safeTrackDir}/{sha256}.zst";

        var trackBlobDir = Path.Combine(_blobsRootDir, safeTrackDir);
        Directory.CreateDirectory(trackBlobDir);
        var targetFilePath = Path.Combine(trackBlobDir, $"{sha256}.zst");

        if (!File.Exists(targetFilePath))
        {
            using var compressor = new Compressor();
            var compressed = compressor.Wrap(rawBytes).ToArray();

            var tempPath = Path.Combine(trackBlobDir, $"{sha256}.tmp.{Guid.NewGuid():N}");
            try
            {
                File.WriteAllBytes(tempPath, compressed);
                File.Move(tempPath, targetFilePath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Best effort temp cleanup
                }

                throw;
            }
        }

        var preview = SqliteJsonUpcaster.TruncateUtf8Safe(rawJson, _previewBytes);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", SqliteJsonUpcaster.CurrentSchemaVersion);
            writer.WriteString("blobRef", blobRef);
            writer.WriteNumber("bytes", rawBytes.Length);
            writer.WriteString("preview", preview);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public JsonElement Hydrate(string? storedJson, string domain)
    {
        var element = _upcaster.UpcastObjectOrElement(storedJson, domain);
        return HydrateIfBlobEnvelope(element);
    }

    public JsonElement? HydrateNullable(string? storedJson, string domain)
    {
        if (string.IsNullOrWhiteSpace(storedJson))
        {
            return null;
        }

        return Hydrate(storedJson, domain);
    }

    private JsonElement HydrateIfBlobEnvelope(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("blobRef", out var blobRefProp) ||
            blobRefProp.ValueKind != JsonValueKind.String)
        {
            return element;
        }

        var blobRef = blobRefProp.GetString();
        var physicalPath = TryResolvePhysicalBlobPath(blobRef);
        if (physicalPath is null || !File.Exists(physicalPath))
        {
            // Return compact envelope/tombstone cleanly if cold blob was pruned or deleted.
            return element;
        }

        var compressedBytes = File.ReadAllBytes(physicalPath);
        using var input = new MemoryStream(compressedBytes, writable: false);
        using var zstd = new DecompressionStream(input);
        using var output = new MemoryStream();
        zstd.CopyTo(output);

        var rawMemory = new ReadOnlyMemory<byte>(output.GetBuffer(), 0, (int)output.Length);
        using var doc = JsonDocument.Parse(rawMemory);
        return doc.RootElement.Clone();
    }

    public static bool TryInspectEnvelope(
        string? storedJson,
        out string? blobRef,
        out long bytes,
        out string? preview,
        out bool isCompacted)
    {
        blobRef = null;
        bytes = 0;
        preview = null;
        isCompacted = false;

        if (string.IsNullOrWhiteSpace(storedJson) || !storedJson.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(storedJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("compacted", out var compProp) && compProp.ValueKind == JsonValueKind.True)
            {
                isCompacted = true;
            }

            if (root.TryGetProperty("blobRef", out var refProp) && refProp.ValueKind == JsonValueKind.String)
            {
                blobRef = refProp.GetString();
            }

            if (root.TryGetProperty("bytes", out var bytesProp) && bytesProp.ValueKind == JsonValueKind.Number && bytesProp.TryGetInt64(out var parsedBytes))
            {
                bytes = parsedBytes;
            }

            if (root.TryGetProperty("preview", out var prevProp) && prevProp.ValueKind == JsonValueKind.String)
            {
                preview = prevProp.GetString();
            }

            return blobRef is not null || isCompacted;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildCompactedTombstone(string rawPayloadJson)
    {
        long originalBytes;
        string previewSource;

        if (TryInspectEnvelope(rawPayloadJson, out _, out var envBytes, out var envPreview, out _) && envBytes > 0)
        {
            originalBytes = envBytes;
            previewSource = envPreview ?? string.Empty;
        }
        else
        {
            originalBytes = Encoding.UTF8.GetByteCount(rawPayloadJson);
            previewSource = rawPayloadJson;
        }

        var preview = SqliteJsonUpcaster.TruncateUtf8Safe(previewSource, TombstonePreviewBytes);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", SqliteJsonUpcaster.CurrentSchemaVersion);
            writer.WriteBoolean("compacted", true);
            writer.WriteNumber("bytes", originalBytes);
            writer.WriteString("preview", preview);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void DeleteTrackBlobsDirectory(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        string safeTrackDir;
        try
        {
            safeTrackDir = ValidateTrackDirectorySegment(trackId);
        }
        catch
        {
            return;
        }

        var trackBlobDir = Path.GetFullPath(Path.Combine(_blobsRootDir, safeTrackDir));
        if (!trackBlobDir.StartsWith(_blobsRootPrefixWithSep, StringComparison.Ordinal))
        {
            return;
        }

        if (Directory.Exists(trackBlobDir))
        {
            Directory.Delete(trackBlobDir, recursive: true);
        }
    }

    public bool DeleteBlobFileIfPresent(string? blobRef)
    {
        var physicalPath = TryResolvePhysicalBlobPath(blobRef);
        if (physicalPath is null || !File.Exists(physicalPath))
        {
            return false;
        }

        File.Delete(physicalPath);

        // Clean up empty track directory if no blobs remain.
        var parentDir = Path.GetDirectoryName(physicalPath);
        if (!string.IsNullOrEmpty(parentDir) &&
            parentDir.StartsWith(_blobsRootPrefixWithSep, StringComparison.Ordinal) &&
            Directory.Exists(parentDir) &&
            !Directory.EnumerateFileSystemEntries(parentDir).Any())
        {
            try
            {
                Directory.Delete(parentDir, recursive: false);
            }
            catch
            {
                // Ignore concurrent dir check
            }
        }

        return true;
    }

    public string? TryResolvePhysicalBlobPath(string? blobRef)
    {
        if (string.IsNullOrWhiteSpace(blobRef))
        {
            return null;
        }

        var normalizedRelative = blobRef.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(_dataDir, normalizedRelative));
        return candidate.StartsWith(_blobsRootPrefixWithSep, StringComparison.Ordinal)
            ? candidate
            : null;
    }

    private static string ValidateTrackDirectorySegment(string trackId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        var trimmed = trackId.Trim();
        if (trimmed is "." or ".." ||
            trimmed.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ArgumentException($"Invalid trackId segment '{trackId}' for cold blob storage.", nameof(trackId));
        }

        return trimmed;
    }
}
