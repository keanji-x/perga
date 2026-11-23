using Dapper;

namespace Perga.Storage.Sqlite;

/// <summary>Track deletion and post-commit cold-blob directory cleanup.</summary>
internal sealed class TrackDeletionRepository
{
    private readonly SqliteStoreContext _context;

    internal TrackDeletionRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public async Task DeleteTrackAsync(string trackId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        _context.EnsureInitialized();

        await _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM tracks WHERE id = @TrackId;",
                        new { TrackId = trackId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        try
        {
            _context.ColdBlobs.DeleteTrackBlobsDirectory(trackId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not clean up blobs for deleted Track {0}: {1}", trackId, ex.Message);
        }

        try
        {
            _context.AttachmentBlobs.DeleteTrackAttachmentsDirectory(trackId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not clean up attachments for deleted Track {0}: {1}", trackId, ex.Message);
        }
    }
}
