using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Wake coalescing, claiming, retry exhaustion and boot recovery.</summary>
internal sealed class TrackWakesRepository
{
    private readonly SqliteStoreContext _context;
    private readonly SessionRepository _sessionRepository;

    internal TrackWakesRepository(SqliteStoreContext context, SessionRepository sessionRepository)
    {
        _context = context;
        _sessionRepository = sessionRepository;
    }

    private const string WakeSelect = """
        SELECT ordinal AS Ordinal, id AS Id, track_id AS TrackId, target_session_id AS TargetSessionId,
            kind AS Kind, coalesce_key AS CoalesceKey, payload_json AS PayloadJson, state AS State,
            reason AS Reason, turn_id AS TurnId, attempts AS Attempts, created_at_ms AS CreatedAtMs,
            updated_at_ms AS UpdatedAtMs FROM track_wakes
        """;

    private const string CriticalKindsSql =
        $"'{TrackWakeKinds.Mail}','{TrackWakeKinds.TaskSettled}','{TrackWakeKinds.AskAnswered}'";

    private const string NonCriticalKindsSql =
        $"'{TrackWakeKinds.Schedule}','{TrackWakeKinds.ReportEdited}','{TrackWakeKinds.Boot}'";

    public Task EnqueueAsync(TrackWakeRequest request, CancellationToken ct) =>
        _context.Writer.EnqueueAsync(async (conn, tx, token) => await EnqueueWakeInternalAsync(conn, tx, request,
            _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(), token).ConfigureAwait(false), ct);

    internal static async Task EnqueueWakeInternalAsync(SqliteConnection conn, SqliteTransaction tx,
        TrackWakeRequest request, long now, CancellationToken ct)
    {
        if (!await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM tracks WHERE id=@TrackId AND closed_at_ms IS NULL);",
            new { request.TrackId }, tx, cancellationToken: ct)).ConfigureAwait(false)) return;
        await conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE track_wakes SET state='{TrackWakeStates.Dropped}', reason='{TrackWakeReasons.Coalesced}', updated_at_ms=@Now
            WHERE track_id=@TrackId AND coalesce_key=@CoalesceKey AND state='{TrackWakeStates.Pending}';
            INSERT INTO track_wakes(id, track_id, target_session_id, kind, coalesce_key, payload_json,
                state, created_at_ms, updated_at_ms)
            VALUES(@Id, @TrackId, @TargetSessionId, @Kind, @CoalesceKey, @PayloadJson, '{TrackWakeStates.Pending}', @Now, @Now);
            """, new { Id = $"wke_{Guid.NewGuid():N}", request.TrackId, request.TargetSessionId,
                request.Kind, request.CoalesceKey, PayloadJson = request.Payload.GetRawText(), Now = now },
            tx, cancellationToken: ct)).ConfigureAwait(false);
    }

    internal static async Task EnqueueWakeOnceInternalAsync(SqliteConnection conn, SqliteTransaction tx,
        TrackWakeRequest request, long now, CancellationToken ct)
    {
        if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM track_wakes WHERE track_id=@TrackId AND coalesce_key=@CoalesceKey);",
            new { request.TrackId, request.CoalesceKey }, tx, cancellationToken: ct)).ConfigureAwait(false)) return;
        await EnqueueWakeInternalAsync(conn, tx, request, now, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrackWake>> ListAsync(string trackId, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return (await conn.QueryAsync<TrackWake>(new CommandDefinition(WakeSelect +
            " WHERE track_id=@TrackId ORDER BY ordinal;", new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<string>> ListPendingTracksAsync(CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return (await conn.QueryAsync<string>(new CommandDefinition($"""
            SELECT DISTINCT w.track_id FROM track_wakes w JOIN tracks t ON t.id=w.track_id
            WHERE w.state='{TrackWakeStates.Pending}' AND t.closed_at_ms IS NULL ORDER BY w.track_id;
            """, cancellationToken: ct)).ConfigureAwait(false)).AsList();
    }

    public Task<IReadOnlyList<TrackWake>> ClaimBatchAsync(string trackId, string targetSessionId, int limit, CancellationToken ct) =>
        _context.Writer.EnqueueAsync<IReadOnlyList<TrackWake>>(async (conn, tx, token) =>
        {
            await DropExhaustedWakesAsync(conn, tx, trackId, null, token).ConfigureAwait(false);
            var wakes = (await conn.QueryAsync<TrackWake>(new CommandDefinition(WakeSelect + $"""
                 WHERE track_id=@TrackId AND state='{TrackWakeStates.Pending}'
                   AND (target_session_id=@TargetSessionId OR (target_session_id IS NULL AND EXISTS(
                       SELECT 1 FROM tracks WHERE id=@TrackId AND lead_session_id=@TargetSessionId)))
                   AND EXISTS(SELECT 1 FROM tracks WHERE id=@TrackId AND closed_at_ms IS NULL)
                 ORDER BY ordinal LIMIT @Limit;
                """, new { TrackId = trackId, TargetSessionId = targetSessionId, Limit = Math.Clamp(limit, 1, 32) },
                tx, cancellationToken: token)).ConfigureAwait(false)).AsList();
            if (wakes.Count == 0) return wakes;
            var now = _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_wakes SET state='{TrackWakeStates.Delivering}', attempts=attempts+1, updated_at_ms=@Now WHERE id IN @Ids;
                """, new { Ids = wakes.Select(w => w.Id), Now = now }, tx, cancellationToken: token)).ConfigureAwait(false);
            return wakes.Select(w => w with { State = TrackWakeStates.Delivering, Attempts = w.Attempts + 1, UpdatedAtMs = now }).ToArray();
        }, ct);

    private async Task DropExhaustedWakesAsync(SqliteConnection conn, SqliteTransaction tx,
        string? trackId, string[]? ids, CancellationToken ct, bool includeDelivering = false)
    {
        var exhausted = (await conn.QueryAsync<TrackWake>(new CommandDefinition(WakeSelect +
            (includeDelivering ? " WHERE state IN ('pending','delivering')" : " WHERE state='pending'") +
            " AND attempts >= 3 AND (@TrackId IS NULL OR track_id=@TrackId)" +
            (ids is null ? ";" : " AND id IN @Ids;"), new { TrackId = trackId, Ids = ids }, tx, cancellationToken: ct)).ConfigureAwait(false)).AsList();
        var now = _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var wake in exhausted)
        {
            var target = wake.TargetSessionId ?? await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT lead_session_id FROM tracks WHERE id=@TrackId;", new { wake.TrackId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (target is not null)
            {
                var item = new SessionItemDto(0, target, null, "system", JsonSerializer.SerializeToElement(new
                {
                    v = 1, content = wake.PayloadJson, wakeId = wake.Id, wakeKind = wake.Kind,
                    status = "interrupted", archived = true, reason = "max_attempts_exceeded"
                }), now);
                await _sessionRepository.InsertSessionItemsAsync(conn, tx, wake.TrackId, [item], ct).ConfigureAwait(false);
            }
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE track_wakes SET state='dropped',reason='max_attempts_exceeded',updated_at_ms=@Now WHERE id=@Id;",
                new { wake.Id, Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    public Task MarkDoneAsync(IReadOnlyList<TrackWake> batch, string reason, string turnId, CancellationToken ct) =>
        TransitionWakesAsync(batch, TrackWakeStates.Done, reason, turnId, ct);

    public Task MarkDroppedAsync(IReadOnlyList<TrackWake> batch, string reason, string? turnId, CancellationToken ct) =>
        TransitionWakesAsync(batch, TrackWakeStates.Dropped, reason, turnId, ct);

    public Task LinkTurnAsync(IReadOnlyList<TrackWake> batch, string turnId, CancellationToken ct) =>
        TransitionWakesAsync(batch, TrackWakeStates.Delivering, null, turnId, ct);

    private Task TransitionWakesAsync(IReadOnlyList<TrackWake> batch, string state, string? reason, string? turnId, CancellationToken ct) =>
        _context.Writer.EnqueueAsync(async (conn, tx, token) =>
        {
            if (batch.Count == 0) return;
            if (reason == "max_attempts_exceeded")
                await DropExhaustedWakesAsync(conn, tx, null, batch.Select(w => w.Id).ToArray(), token, includeDelivering: true).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_wakes SET state=@State, reason=@Reason, turn_id=COALESCE(@TurnId,turn_id), updated_at_ms=@Now
                WHERE id IN @Ids AND state='{TrackWakeStates.Delivering}';
                """, new { Ids = batch.Select(w => w.Id), State = state, Reason = reason, TurnId = turnId,
                    Now = _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() }, tx, cancellationToken: token)).ConfigureAwait(false);
        }, ct);

    public Task<bool> CancelPendingWakeAsync(string trackId, string wakeId, CancellationToken ct) =>
        _context.Writer.EnqueueAsync(async (conn, tx, token) => await conn.ExecuteAsync(new CommandDefinition($"""
            UPDATE track_wakes SET state='{TrackWakeStates.Dropped}', reason='{TrackWakeReasons.Canceled}', updated_at_ms=@Now
            WHERE track_id=@TrackId AND id=@Id AND state='{TrackWakeStates.Pending}';
            """, new { TrackId = trackId, Id = wakeId, Now = _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() },
            tx, cancellationToken: token)).ConfigureAwait(false) == 1, ct);

    public Task<IReadOnlyList<string>> RecoverOnBootAsync(CancellationToken ct) =>
        _context.Writer.EnqueueAsync<IReadOnlyList<string>>(async (conn, tx, token) =>
        {
            var affected = (await conn.QueryAsync<string>(new CommandDefinition(
                $"SELECT DISTINCT track_id FROM track_wakes WHERE state='{TrackWakeStates.Delivering}';", transaction: tx, cancellationToken: token)).ConfigureAwait(false)).AsList();
            await DropExhaustedWakesAsync(conn, tx, null, null, token, includeDelivering: true).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_wakes AS w SET state='{TrackWakeStates.Dropped}', reason='{TrackWakeReasons.BootSuperseded}', updated_at_ms=@Now
                WHERE w.state='{TrackWakeStates.Delivering}' AND w.coalesce_key IS NOT NULL AND EXISTS (
                    SELECT 1 FROM track_wakes n WHERE n.track_id=w.track_id AND n.coalesce_key=w.coalesce_key
                      AND (n.state='{TrackWakeStates.Pending}' OR (n.state='{TrackWakeStates.Delivering}' AND n.ordinal>w.ordinal)));
                UPDATE track_wakes SET state='{TrackWakeStates.Dropped}', reason='{TrackWakeReasons.BootDropped}', updated_at_ms=@Now
                WHERE state='{TrackWakeStates.Delivering}' AND kind IN ({NonCriticalKindsSql});
                UPDATE track_wakes SET state='{TrackWakeStates.Pending}', reason='{TrackWakeReasons.BootRequeued}', turn_id=NULL, updated_at_ms=@Now
                WHERE state='{TrackWakeStates.Delivering}' AND kind IN ({CriticalKindsSql});
                """, new { Now = _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() }, tx, cancellationToken: token)).ConfigureAwait(false);
            return affected;
        }, ct);
}
