using Dapper;

namespace Perga.Storage.Sqlite;

/// <summary>Track deletion and post-commit file cleanup.</summary>
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

        _context.DeleteTrackFiles(trackId);
    }
}
