using Dapper;
using Perga.Contracts;

namespace Perga.Storage.Sqlite;

/// <summary>Terminal persistence, deletion and reverse Track lookup.</summary>
internal sealed class TerminalRepository
{
    private readonly SqliteStoreContext _context;

    internal TerminalRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public Task UpsertTerminalAsync(TerminalDto terminal, EventEnvelopeDto? evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        _context.EnsureInitialized();

        var status = SqliteJsonUpcaster.FormatTerminalStatus(terminal.Status);

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO terminals (
                            id, track_id, session_id, idempotency_key, title,
                            program, cwd, status, exit_code, created_at_ms, exited_at_ms
                        )
                        VALUES (
                            @Id, @TrackId, @SessionId, @IdempotencyKey, @Title,
                            @Program, @Cwd, @Status, @ExitCode, @CreatedAtMs, @ExitedAtMs
                        )
                        ON CONFLICT(id) DO UPDATE SET
                            session_id = excluded.session_id,
                            idempotency_key = excluded.idempotency_key,
                            title = excluded.title,
                            program = excluded.program,
                            cwd = excluded.cwd,
                            status = excluded.status,
                            exit_code = excluded.exit_code,
                            exited_at_ms = excluded.exited_at_ms;
                        """,
                        new
                        {
                            terminal.Id,
                            terminal.TrackId,
                            terminal.SessionId,
                            terminal.IdempotencyKey,
                            terminal.Title,
                            terminal.Program,
                            terminal.Cwd,
                            Status = status,
                            terminal.ExitCode,
                            terminal.CreatedAtMs,
                            terminal.ExitedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                if (evt is not null)
                {
                    await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }

    public Task DeleteTerminalAsync(string trackId, string terminalId, EventEnvelopeDto? evt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        _context.EnsureInitialized();

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM terminals WHERE id = @TerminalId AND track_id = @TrackId;",
                        new { TerminalId = terminalId, TrackId = trackId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                if (evt is not null)
                {
                    await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }

    public async Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(string trackId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        _context.EnsureInitialized();

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TerminalRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       track_id AS TrackId,
                       session_id AS SessionId,
                       idempotency_key AS IdempotencyKey,
                       title AS Title,
                       program AS Program,
                       cwd AS Cwd,
                       status AS Status,
                       exit_code AS ExitCode,
                       created_at_ms AS CreatedAtMs,
                       exited_at_ms AS ExitedAtMs
                FROM terminals
                WHERE track_id = @TrackId
                ORDER BY created_at_ms ASC, id ASC;
                """,
                new { TrackId = trackId },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapTerminal).ToList();
    }

    public async Task<string?> FindTrackIdByTerminalAsync(string terminalId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        _context.EnsureInitialized();

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT track_id FROM terminals WHERE id = @TerminalId;",
                new { TerminalId = terminalId },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    private static TerminalDto MapTerminal(TerminalRow r) =>
        new(
            Id: r.Id,
            TrackId: r.TrackId,
            SessionId: r.SessionId,
            IdempotencyKey: r.IdempotencyKey,
            Title: r.Title,
            Program: r.Program,
            Cwd: r.Cwd,
            Status: SqliteJsonUpcaster.ParseTerminalStatus(r.Status),
            ExitCode: r.ExitCode.HasValue ? (int)r.ExitCode.Value : null,
            CreatedAtMs: r.CreatedAtMs,
            ExitedAtMs: r.ExitedAtMs);

    private sealed class TerminalRow
    {
        public string Id { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string? SessionId { get; init; }
        public string? IdempotencyKey { get; init; }
        public string Title { get; init; } = "Terminal";
        public string? Program { get; init; }
        public string Cwd { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public long? ExitCode { get; init; }
        public long CreatedAtMs { get; init; }
        public long? ExitedAtMs { get; init; }
    }
}
