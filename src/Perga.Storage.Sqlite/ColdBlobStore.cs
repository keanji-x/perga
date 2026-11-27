using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Perga.Kernel;
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

        return WriteColdBlobAndBuildEnvelope(trackId, payload, rawJson, rawBytes);
    }

    public string? StoreOrInlineNullable(string trackId, JsonElement? payload)
    {
        if (payload is not { ValueKind: not JsonValueKind.Undefined } value)
        {
            return null;
        }

        return StoreOrInline(trackId, value);
    }

    private string WriteColdBlobAndBuildEnvelope(string trackId, JsonElement payload, string rawJson, byte[] rawBytes)
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
            WriteToolResultIdentity(writer, payload);
            WriteTextPreview(writer, payload);
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
        if (physicalPath is null)
        {
            return element;
        }

        try
        {
            using var input = File.Open(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var zstd = new DecompressionStream(input);
            using var output = new MemoryStream();
            zstd.CopyTo(output);

            var rawMemory = new ReadOnlyMemory<byte>(output.GetBuffer(), 0, (int)output.Length);
            using var doc = JsonDocument.Parse(rawMemory);
            return doc.RootElement.Clone();
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // A reader can hold an older reference after maintenance committed.
            return element;
        }
    }

    public IEnumerable<string> EnumerateBlobRefs()
    {
        if (!Directory.Exists(_blobsRootDir))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 1,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in Directory.EnumerateFiles(_blobsRootDir, "*.zst", options))
        {
            var parent = Path.GetDirectoryName(file);
            var name = Path.GetFileNameWithoutExtension(file);
            if (parent is not null && parent != _blobsRootDir && name.Length == 64 && name.All(char.IsAsciiHexDigit))
            {
                yield return $"blobs/{Path.GetFileName(parent)}/{Path.GetFileName(file)}";
            }
        }
    }

    public static string BuildCompactedTombstone(string rawPayloadJson)
    {
        using var doc = JsonDocument.Parse(rawPayloadJson);
        var payload = doc.RootElement;
        var originalBytes = (long)Encoding.UTF8.GetByteCount(rawPayloadJson);
        var previewSource = rawPayloadJson;
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("blobRef", out var reference) && reference.ValueKind == JsonValueKind.String &&
            payload.TryGetProperty("bytes", out var bytes) && bytes.TryGetInt64(out var blobBytes))
        {
            originalBytes = blobBytes;
            previewSource = payload.TryGetProperty("preview", out var storedPreview) && storedPreview.ValueKind == JsonValueKind.String
                ? storedPreview.GetString() ?? string.Empty
                : string.Empty;
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
            if (WriteToolResultIdentity(writer, payload))
            {
                writer.WriteStartObject("output");
                writer.WriteBoolean("compacted", true);
                writer.WriteString("preview", preview);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void WriteTextPreview(Utf8JsonWriter writer, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            return;

        string? value;
        try { value = text.GetString(); }
        catch (InvalidOperationException)
        {
            // Raw JSON can preserve unpaired surrogates even when the string cannot be decoded.
            return;
        }

        if (TrackOverlayPreview.Normalize(value) is { } preview)
            writer.WriteString("textPreview", preview);
    }

    private static bool WriteToolResultIdentity(Utf8JsonWriter writer, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("callId", out var callId) || callId.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("isError", out var isError) || isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        writer.WriteString("callId", callId.GetString());
        writer.WriteBoolean("isError", isError.GetBoolean());
        return true;
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
