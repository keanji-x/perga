using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;

namespace Perga.Storage.Sqlite;

/// <summary>The event ledger and its transaction-local append operation.</summary>
internal sealed class EventRepository
{
    private readonly SqliteStoreContext _context;

    internal EventRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public Task AppendEventAsync(EventEnvelopeDto evt, CancellationToken ct) =>
        AppendEventWithIdAsync(evt, ct);

    public Task<EventEnvelopeDto> AppendEventWithIdAsync(EventEnvelopeDto evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        _context.EnsureInitialized();

        return _context.Writer.EnqueueAsync<EventEnvelopeDto>(
            async (conn, tx, innerCt) =>
            {
                var id = await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                return evt with { EventId = id };
            },
            ct);
    }

    public async Task<long> GetEventTipIdAsync(CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            // AUTOINCREMENT retains the last committed identity after maintenance deletes rows.
            "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'events'), 0)", cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EventEnvelopeDto>> ListEventsSinceAsync(
        long sinceEventId,
        string? trackId = null,
        int limit = 500,
        CancellationToken ct = default)
    {
        _context.EnsureInitialized();
        var clampedLimit = Math.Max(1, limit);

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var sql = string.IsNullOrWhiteSpace(trackId)
            ? """
              SELECT id AS Id,
                     area_id AS AreaId,
                     track_id AS TrackId,
                     kind AS Kind,
                     payload_json AS PayloadJson,
                     created_at_ms AS CreatedAtMs
              FROM events
              WHERE id > @SinceEventId
              ORDER BY id ASC
              LIMIT @Limit;
              """
            : """
              SELECT id AS Id,
                     area_id AS AreaId,
                     track_id AS TrackId,
                     kind AS Kind,
                     payload_json AS PayloadJson,
                     created_at_ms AS CreatedAtMs
              FROM events
              WHERE track_id = @TrackId AND id > @SinceEventId
              ORDER BY id ASC
              LIMIT @Limit;
              """;

        var rows = await conn.QueryAsync<EventRow>(
            new CommandDefinition(
                sql,
                new
                {
                    SinceEventId = sinceEventId,
                    TrackId = trackId,
                    Limit = clampedLimit
                },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapEvent).ToList();
    }

    internal static Task<long> InsertEventInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        EventEnvelopeDto evt,
        CancellationToken ct)
    {
        return conn.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO events (area_id, track_id, kind, payload_json, created_at_ms)
                VALUES (@AreaId, @TrackId, @Kind, @PayloadJson, @CreatedAtMs);
                SELECT last_insert_rowid();
                """,
                new
                {
                    evt.AreaId,
                    evt.TrackId,
                    evt.Kind,
                    PayloadJson = SqliteJsonUpcaster.SerializeElement(evt.Payload),
                    evt.CreatedAtMs
                },
                transaction: tx,
                cancellationToken: ct));
    }

    private EventEnvelopeDto MapEvent(EventRow r) =>
        new(
            EventId: r.Id,
            AreaId: r.AreaId,
            TrackId: r.TrackId,
            Kind: r.Kind,
            Payload: _context.Upcaster.UpcastObjectOrElement(r.PayloadJson, "events.payload_json"),
            CreatedAtMs: r.CreatedAtMs);

    private sealed class EventRow
    {
        public long Id { get; init; }
        public string? AreaId { get; init; }
        public string? TrackId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = "{}";
        public long CreatedAtMs { get; init; }
    }
}
