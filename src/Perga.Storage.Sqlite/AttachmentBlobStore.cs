using System.Security.Cryptography;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

internal sealed record WrittenAttachmentBlob(
    string AttachmentId,
    string ContentType,
    string Extension,
    long SizeBytes,
    string Sha256,
    string StorageRelPath,
    string PhysicalPath);

internal sealed record PromotedAttachmentLink(
    string AttachmentId,
    string OldStorageRelPath,
    string NewStorageRelPath,
    string NewPhysicalPath,
    bool CreatedNew,
    bool Pinned = false);

internal sealed record EnumeratedAttachmentFile(
    string StorageRelPath,
    string PhysicalPath,
    bool IsTempFile);

/// <summary>
/// Manages Host-owned binary image attachments under <c>{dataDir}/attachments/_staged</c> and
/// <c>{dataDir}/attachments/{trackId}</c> with magic-byte sniffing, streaming size limits,
/// atomic writes, symlink/path-traversal containment, crash-safe hard-link promotion, and O(1) track cleanup.
/// </summary>
internal sealed class AttachmentBlobStore
{
    public const string StagedDirectoryName = "_staged";
    private const int StreamBufferSize = 64 * 1024;
    private const int HeaderSniffBytes = 12;

    private readonly string _dataDir;
    private readonly string _attachmentsRootDir;
    private readonly string _attachmentsRootPrefixWithSep;
    private readonly object _pinGate = new();
    private readonly Dictionary<string, int> _pinnedRelPaths = new(StringComparer.Ordinal);

    public AttachmentBlobStore(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dataDir = Path.GetFullPath(options.ResolveDataDir());
        _attachmentsRootDir = Path.GetFullPath(options.ResolveAttachmentsRootDir());
        _attachmentsRootPrefixWithSep = _attachmentsRootDir.EndsWith(Path.DirectorySeparatorChar)
            ? _attachmentsRootDir
            : _attachmentsRootDir + Path.DirectorySeparatorChar;
    }

    public string AttachmentsRootDir => _attachmentsRootDir;

    public void PinRelativePath(string storageRelPath)
    {
        if (string.IsNullOrWhiteSpace(storageRelPath))
        {
            return;
        }

        lock (_pinGate)
        {
            _pinnedRelPaths.TryGetValue(storageRelPath, out var count);
            _pinnedRelPaths[storageRelPath] = count + 1;
        }
    }

    public void UnpinRelativePath(string storageRelPath)
    {
        if (string.IsNullOrWhiteSpace(storageRelPath))
        {
            return;
        }

        lock (_pinGate)
        {
            if (!_pinnedRelPaths.TryGetValue(storageRelPath, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _pinnedRelPaths.Remove(storageRelPath);
            }
            else
            {
                _pinnedRelPaths[storageRelPath] = count - 1;
            }
        }
    }

    public bool IsPinned(string storageRelPath)
    {
        if (string.IsNullOrWhiteSpace(storageRelPath))
        {
            return false;
        }

        lock (_pinGate)
        {
            return _pinnedRelPaths.TryGetValue(storageRelPath, out var count) && count > 0;
        }
    }

    /// <summary>
    /// Atomically checks that <paramref name="storageRelPath"/> is not currently pinned by an in-flight
    /// write or promotion and deletes it under the same critical section.
    /// </summary>
    public bool DeleteUnpinnedFileBestEffort(string? storageRelPath)
    {
        if (string.IsNullOrWhiteSpace(storageRelPath))
        {
            return false;
        }

        lock (_pinGate)
        {
            if (_pinnedRelPaths.TryGetValue(storageRelPath, out var count) && count > 0)
            {
                return false;
            }

            return DeleteFileBestEffort(storageRelPath);
        }
    }

    private void PinRelativePathLocked(string storageRelPath)
    {
        _pinnedRelPaths.TryGetValue(storageRelPath, out var count);
        _pinnedRelPaths[storageRelPath] = count + 1;
    }

    private void UnpinRelativePathLocked(string storageRelPath)
    {
        if (!_pinnedRelPaths.TryGetValue(storageRelPath, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _pinnedRelPaths.Remove(storageRelPath);
        }
        else
        {
            _pinnedRelPaths[storageRelPath] = count - 1;
        }
    }

    private bool HasPinnedPathInDirectoryLocked(string directorySegment)
    {
        var prefix = $"attachments/{directorySegment}/";
        foreach (var key in _pinnedRelPaths.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TrySniffImageMime(ReadOnlySpan<byte> header, out string contentType, out string extension)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            contentType = "image/png";
            extension = ".png";
            return true;
        }

        // JPEG: FF D8 FF
        if (header.Length >= 3 &&
            header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            contentType = "image/jpeg";
            extension = ".jpg";
            return true;
        }

        // GIF: GIF87a or GIF89a
        if (header.Length >= 6 &&
            header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x38 &&
            (header[4] == 0x37 || header[4] == 0x39) && header[5] == 0x61)
        {
            contentType = "image/gif";
            extension = ".gif";
            return true;
        }

        // WebP: RIFF....WEBP
        if (header.Length >= 12 &&
            header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
        {
            contentType = "image/webp";
            extension = ".webp";
            return true;
        }

        contentType = string.Empty;
        extension = string.Empty;
        return false;
    }

    public async Task<WrittenAttachmentBlob> WriteStagedAsync(
        string? trackId,
        Stream source,
        long maxBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var limit = Math.Max(1L, maxBytes);
        var dirSegment = trackId is null ? StagedDirectoryName : ValidateTrackSegmentOrThrow(trackId);
        var attachmentId = $"att_{Guid.NewGuid():N}";
        var sniffed = false;
        var contentType = string.Empty;
        var extension = string.Empty;
        long totalBytes = 0;
        var headerBuf = new byte[HeaderSniffBytes];
        var headerLen = 0;
        var readBuf = new byte[StreamBufferSize];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        string targetDir;
        string tempPath;
        FileStream fs;
        lock (_pinGate)
        {
            // Keep directory creation and temp-file creation serialized with TryPruneEmptyTrackDirectory
            // so concurrent orphan cleanup cannot remove an empty track directory before tempPath is created.
            targetDir = EnsureContainedDirectory(dirSegment);
            tempPath = Path.Combine(targetDir, $".{attachmentId}.tmp.{Guid.NewGuid():N}");
            fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize, useAsync: true);
        }

        try
        {
            await using (fs)
            {
                while (true)
                {
                    var read = await source.ReadAsync(readBuf.AsMemory(), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (headerLen < HeaderSniffBytes)
                    {
                        var take = Math.Min(read, HeaderSniffBytes - headerLen);
                        Buffer.BlockCopy(readBuf, 0, headerBuf, headerLen, take);
                        headerLen += take;
                        if (headerLen == HeaderSniffBytes)
                        {
                            if (!TrySniffImageMime(headerBuf.AsSpan(0, headerLen), out contentType, out extension))
                            {
                                throw KernelException.InvalidArgument(
                                    "Unsupported or invalid image format. Only PNG, JPEG, GIF, and WebP images are supported.");
                            }
                            sniffed = true;
                        }
                    }

                    totalBytes += read;
                    if (totalBytes > limit)
                    {
                        throw KernelException.PayloadTooLarge(
                            $"Image attachment exceeds maximum allowed size of {limit} bytes.");
                    }

                    hasher.AppendData(readBuf, 0, read);
                    await fs.WriteAsync(readBuf.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            if (totalBytes == 0)
            {
                throw KernelException.InvalidArgument("Image attachment body cannot be empty.");
            }

            if (!sniffed && !TrySniffImageMime(headerBuf.AsSpan(0, headerLen), out contentType, out extension))
            {
                throw KernelException.InvalidArgument(
                    "Unsupported or invalid image format. Only PNG, JPEG, GIF, and WebP images are supported.");
            }

            var sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());
            var finalFileName = $"{attachmentId}{extension}";
            var finalRelPath = $"attachments/{dirSegment}/{finalFileName}";
            var finalPhysPath = ResolveTargetPathOrThrow(targetDir, finalFileName);
            lock (_pinGate)
            {
                PinRelativePathLocked(finalRelPath);
                try
                {
                    File.Move(tempPath, finalPhysPath, overwrite: false);
                }
                catch
                {
                    UnpinRelativePathLocked(finalRelPath);
                    throw;
                }
            }

            return new WrittenAttachmentBlob(
                attachmentId,
                contentType,
                extension,
                totalBytes,
                sha256,
                finalRelPath,
                finalPhysPath);
        }
        catch
        {
            TryDeleteFileQuietly(tempPath);
            throw;
        }
    }

    public PromotedAttachmentLink LinkFromStagedToTrack(
        string attachmentId,
        string sourceStorageRelPath,
        string trackId,
        string contentType,
        bool pinTarget = true)
    {
        if (!SessionAttachmentValidation.IsValidAttachmentId(attachmentId))
        {
            throw KernelException.InvalidArgument($"Invalid attachment ID '{attachmentId}'.");
        }

        var extension = SessionAttachmentValidation.ExtensionForContentType(contentType)
            ?? throw KernelException.InvalidArgument($"Unsupported attachment content type '{contentType}'.");
        var srcPhysPath = TryResolveExistingPhysicalPath(sourceStorageRelPath)
            ?? throw KernelException.NotFound($"Attachment blob for '{attachmentId}' was not found on disk.");

        var safeTrack = ValidateTrackSegmentOrThrow(trackId);
        var fileName = $"{attachmentId}{extension}";
        var targetRelPath = $"attachments/{safeTrack}/{fileName}";
        var targetDirCandidate = Path.GetFullPath(Path.Combine(_attachmentsRootDir, safeTrack));
        if (!targetDirCandidate.StartsWith(_attachmentsRootPrefixWithSep, StringComparison.Ordinal))
        {
            throw KernelException.Forbidden("Attachment directory escapes attachments root.");
        }

        var targetPhysPath = ResolveTargetPathOrThrow(targetDirCandidate, fileName);

        if (string.Equals(srcPhysPath, targetPhysPath, StringComparison.Ordinal))
        {
            return new PromotedAttachmentLink(attachmentId, sourceStorageRelPath, targetRelPath, targetPhysPath, CreatedNew: false, Pinned: false);
        }

        // Hold _pinGate across pin + directory ensure + existence check + hard-link creation so concurrent
        // DeleteUnpinnedFileBestEffort / TryPruneEmptyTrackDirectory cannot unlink targetPhysPath or prune
        // its parent directory between directory preparation and hard-link creation.
        bool needsCrossDeviceFallback = false;
        string targetDir;
        lock (_pinGate)
        {
            if (pinTarget)
            {
                PinRelativePathLocked(targetRelPath);
            }

            try
            {
                targetDir = EnsureContainedDirectory(safeTrack);
                targetPhysPath = ResolveTargetPathOrThrow(targetDir, fileName);

                if (File.Exists(targetPhysPath))
                {
                    EnsureNotSymlink(targetPhysPath);
                    return new PromotedAttachmentLink(attachmentId, sourceStorageRelPath, targetRelPath, targetPhysPath, CreatedNew: false, Pinned: pinTarget);
                }

                try
                {
                    File.CreateHardLink(targetPhysPath, srcPhysPath);
                    return new PromotedAttachmentLink(attachmentId, sourceStorageRelPath, targetRelPath, targetPhysPath, CreatedNew: true, Pinned: pinTarget);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    if (File.Exists(targetPhysPath))
                    {
                        EnsureNotSymlink(targetPhysPath);
                        return new PromotedAttachmentLink(attachmentId, sourceStorageRelPath, targetRelPath, targetPhysPath, CreatedNew: false, Pinned: pinTarget);
                    }

                    needsCrossDeviceFallback = true;
                }
            }
            catch
            {
                if (pinTarget)
                {
                    UnpinRelativePathLocked(targetRelPath);
                }
                throw;
            }
        }

        // Cross-device fallback runs while targetRelPath remains pinned, so GC cannot delete the target.
        if (needsCrossDeviceFallback)
        {
            var tmpCopy = Path.Combine(targetDir, $".{attachmentId}.promote.{Guid.NewGuid():N}");
            try
            {
                File.Copy(srcPhysPath, tmpCopy, overwrite: false);
                File.Move(tmpCopy, targetPhysPath, overwrite: false);
                return new PromotedAttachmentLink(attachmentId, sourceStorageRelPath, targetRelPath, targetPhysPath, CreatedNew: true, Pinned: pinTarget);
            }
            catch
            {
                TryDeleteFileQuietly(tmpCopy);
                if (pinTarget)
                {
                    UnpinRelativePath(targetRelPath);
                }
                throw;
            }
        }

        return new PromotedAttachmentLink(attachmentId, sourceStorageRelPath, targetRelPath, targetPhysPath, CreatedNew: false, Pinned: pinTarget);
    }

    public Stream? OpenReadStream(string? storageRelPath)
    {
        var physicalPath = TryResolveExistingPhysicalPath(storageRelPath);
        if (physicalPath is null)
        {
            return null;
        }

        try
        {
            return File.Open(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public string? TryResolvePhysicalPath(string? storageRelPath)
    {
        if (string.IsNullOrWhiteSpace(storageRelPath))
        {
            return null;
        }

        var normalized = storageRelPath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(_dataDir, normalized));
        if (!candidate.StartsWith(_attachmentsRootPrefixWithSep, StringComparison.Ordinal))
        {
            return null;
        }

        // Ensure attachments root and parent directory are not symlinks.
        if (IsReparsePoint(_attachmentsRootDir))
        {
            return null;
        }

        var parentDir = Path.GetDirectoryName(candidate);
        if (string.IsNullOrEmpty(parentDir) ||
            (!string.Equals(parentDir, _attachmentsRootDir, StringComparison.Ordinal) &&
             !parentDir.StartsWith(_attachmentsRootPrefixWithSep, StringComparison.Ordinal)) ||
            IsReparsePoint(parentDir))
        {
            return null;
        }

        if (IsReparsePoint(candidate))
        {
            return null;
        }

        return candidate;
    }

    public string? TryResolveExistingPhysicalPath(string? storageRelPath)
    {
        var candidate = TryResolvePhysicalPath(storageRelPath);
        if (candidate is null || !File.Exists(candidate) || IsReparsePoint(candidate))
        {
            return null;
        }

        return candidate;
    }

    public bool DeleteFileIfPresent(string? storageRelPath)
    {
        var physicalPath = TryResolveExistingPhysicalPath(storageRelPath);
        if (physicalPath is null)
        {
            return false;
        }

        try
        {
            File.Delete(physicalPath);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        TryPruneEmptyTrackDirectory(Path.GetDirectoryName(physicalPath));
        return true;
    }

    public bool DeleteFileBestEffort(string? storageRelPath)
    {
        try
        {
            return DeleteFileIfPresent(storageRelPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void DeleteTrackAttachmentsDirectory(string trackId)
    {
        if (!SessionAttachmentValidation.IsValidTrackSegment(trackId))
        {
            return;
        }

        var safeTrack = trackId.Trim();
        var trackDir = Path.GetFullPath(Path.Combine(_attachmentsRootDir, safeTrack));
        if (!trackDir.StartsWith(_attachmentsRootPrefixWithSep, StringComparison.Ordinal) ||
            IsReparsePoint(_attachmentsRootDir) ||
            IsReparsePoint(trackDir))
        {
            return;
        }

        if (Directory.Exists(trackDir))
        {
            Directory.Delete(trackDir, recursive: true);
        }
    }

    public IEnumerable<EnumeratedAttachmentFile> EnumerateAttachmentFilesOlderThan(DateTimeOffset cutoffUtc)
    {
        if (!Directory.Exists(_attachmentsRootDir) || IsReparsePoint(_attachmentsRootDir))
        {
            yield break;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 1,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };

        foreach (var file in Directory.EnumerateFiles(_attachmentsRootDir, "*", options))
        {
            var parent = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, _attachmentsRootDir, StringComparison.Ordinal) ||
                IsReparsePoint(parent) ||
                IsReparsePoint(file))
            {
                continue;
            }

            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (lastWriteUtc >= cutoffUtc.UtcDateTime)
            {
                continue;
            }

            var parentName = Path.GetFileName(parent);
            var fileName = Path.GetFileName(file);
            var isTemp = fileName.StartsWith('.');
            var relPath = $"attachments/{parentName}/{fileName}";
            if (!isTemp && IsPinned(relPath))
            {
                continue;
            }

            yield return new EnumeratedAttachmentFile(relPath, file, isTemp);
        }
    }

    public void TryPruneEmptyTrackDirectory(string? dirPath)
    {
        if (string.IsNullOrEmpty(dirPath) ||
            !dirPath.StartsWith(_attachmentsRootPrefixWithSep, StringComparison.Ordinal))
        {
            return;
        }

        var dirName = Path.GetFileName(dirPath);
        if (string.IsNullOrEmpty(dirName) ||
            string.Equals(dirName, StagedDirectoryName, StringComparison.Ordinal))
        {
            return;
        }

        lock (_pinGate)
        {
            if (HasPinnedPathInDirectoryLocked(dirName) ||
                IsReparsePoint(dirPath) ||
                !Directory.Exists(dirPath))
            {
                return;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(dirPath).Any())
                {
                    Directory.Delete(dirPath, recursive: false);
                }
            }
            catch
            {
                // Ignore concurrent directory updates.
            }
        }
    }

    private string EnsureContainedDirectory(string segment)
    {
        Directory.CreateDirectory(_attachmentsRootDir);
        EnsureNotSymlink(_attachmentsRootDir);
        var targetDir = Path.GetFullPath(Path.Combine(_attachmentsRootDir, segment));
        if (!targetDir.StartsWith(_attachmentsRootPrefixWithSep, StringComparison.Ordinal))
        {
            throw KernelException.Forbidden("Attachment directory escapes attachments root.");
        }

        Directory.CreateDirectory(targetDir);
        EnsureNotSymlink(targetDir);
        return targetDir;
    }

    private string ResolveTargetPathOrThrow(string containedDir, string fileName)
    {
        var candidate = Path.GetFullPath(Path.Combine(containedDir, fileName));
        if (!candidate.StartsWith(_attachmentsRootPrefixWithSep, StringComparison.Ordinal))
        {
            throw KernelException.Forbidden("Attachment file escapes attachments root.");
        }

        return candidate;
    }

    private static string ValidateTrackSegmentOrThrow(string trackId)
    {
        if (!SessionAttachmentValidation.IsValidTrackSegment(trackId))
        {
            throw KernelException.InvalidArgument($"Invalid trackId segment '{trackId}' for attachment storage.");
        }

        return trackId.Trim();
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void EnsureNotSymlink(string path)
    {
        if (IsReparsePoint(path))
        {
            throw KernelException.Forbidden("Symlinks are not permitted inside attachment storage.");
        }
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}
