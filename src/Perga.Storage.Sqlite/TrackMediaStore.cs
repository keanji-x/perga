using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

internal sealed class TrackMediaStore(string dataDir)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(dataDir, "media");

    public static string? Serialize(IReadOnlyList<StoredImageAttachment> attachments) =>
        attachments.Count == 0 ? null : JsonSerializer.Serialize(attachments, JsonOptions);

    public static IReadOnlyList<StoredImageAttachment> Deserialize(string? json)
    {
        if (json is null) return [];
        var images = JsonSerializer.Deserialize<StoredImageAttachment[]>(json, JsonOptions);
        if (images is null || images.Length > ImageAttachmentContract.MaxAttachmentsPerMessage ||
            images.Any(image => image is null || !ImageAttachmentContract.TryParseMediaFileName(image.Id, out var mediaType)
                || image.ContentType != mediaType || image.Size <= 0 || image.Size > ImageAttachmentContract.MaxAttachmentBytes)
            || images.Sum(image => image.Size) > ImageAttachmentContract.MaxTotalBytes)
            throw new InvalidDataException("Invalid stored image metadata.");
        return images;
    }

    public static Task<bool> IsReferencedAsync(SqliteConnection conn, SqliteTransaction? tx, string trackId,
        string fileName, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM track_media_refs WHERE track_id = @TrackId AND file_name = @FileName);
            """, new { TrackId = trackId, FileName = fileName }, tx, cancellationToken: ct));

    public async Task SaveAsync(string trackId, IReadOnlyList<ImageMediaWrite> images, List<string> created, CancellationToken ct)
    {
        if (!IsTrackSegment(trackId))
            throw new ArgumentException("Invalid Track media directory segment.", nameof(trackId));
        var directory = Path.Combine(_root, trackId);
        Directory.CreateDirectory(directory);
        foreach (var image in images)
        {
            if (!ImageAttachmentContract.TryParseMediaFileName(image.Attachment.Id, out _))
                throw new ArgumentException("Invalid media filename.", nameof(images));
            var path = Path.Combine(directory, image.Attachment.Id);
            if (File.Exists(path)) continue;
            var temp = path + $".tmp.{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllBytesAsync(temp, image.Bytes, ct).ConfigureAwait(false);
                File.Move(temp, path);
                created.Add(image.Attachment.Id);
            }
            finally
            {
                TryDelete(() => File.Delete(temp), temp);
            }
        }
    }

    public TrackMedia? Open(string trackId, string fileName, string mediaType)
    {
        try
        {
            return new(File.Open(Path.Combine(_root, trackId, fileName), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete), mediaType);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static bool IsTrackSegment(string trackId) =>
        trackId.Length > 0 && trackId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public async Task CleanupUncommittedAsync(SqliteConnection conn, string trackId, IReadOnlyList<string> created)
    {
        if (created.Count == 0) return;
        try
        {
            // Keep the absence check and removal under the database write lock as well as the local writer.
            using var tx = conn.BeginTransaction(deferred: false);
            foreach (var fileName in created)
            {
                if (!await IsReferencedAsync(conn, tx, trackId, fileName, CancellationToken.None).ConfigureAwait(false))
                    TryDelete(() => File.Delete(Path.Combine(_root, trackId, fileName)), fileName);
            }
            tx.Commit();
        }
        catch (SqliteException ex)
        {
            System.Diagnostics.Trace.TraceWarning("Could not check uncommitted Track media for {0}: {1}", trackId, ex.Message);
        }
    }

    public void DeleteTrack(string trackId)
    {
        if (!IsTrackSegment(trackId)) return;
        var path = Path.Combine(_root, trackId);
        TryDelete(() =>
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }, path);
    }

    public async Task SweepDeletedTracksAsync(SqliteConnection conn, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            using var tx = conn.BeginTransaction(deferred: false);
            foreach (var path in Directory.EnumerateDirectories(_root))
            {
                ct.ThrowIfCancellationRequested();
                var trackId = Path.GetFileName(path);
                var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT EXISTS(SELECT 1 FROM tracks WHERE id = @TrackId);
                    """, new { TrackId = trackId }, tx, cancellationToken: ct)).ConfigureAwait(false);
                if (!exists) DeleteTrack(trackId);
            }
            tx.Commit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not sweep deleted Track media: {0}", ex.Message);
        }
    }

    private static void TryDelete(Action remove, string path)
    {
        try { remove(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not clean up Track media {0}: {1}", path, ex.Message);
        }
    }
}
