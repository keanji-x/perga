using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore
{
    public async Task<IReadOnlyList<ProviderSessionResumeFence>> ListSessionResumeFencesAsync(string trackId, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var fences = await conn.QueryAsync<(string SessionId, string TurnId, string NativeId, long Quiescent)>(new CommandDefinition(
            """
            SELECT r.session_id AS SessionId, r.turn_id AS TurnId, r.native_id AS NativeId, r.quiescent AS Quiescent
            FROM provider_session_resume r JOIN agent_sessions s ON s.id = r.session_id
            WHERE s.track_id = @TrackId;
            """, new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false);
        return fences.Select(fence => new ProviderSessionResumeFence(fence.SessionId, fence.TurnId, fence.NativeId, fence.Quiescent != 0)).ToArray();
    }

    public Task SaveSessionResumeFenceAsync(ProviderSessionResumeFence fence, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            RequireOne(await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO provider_session_resume (session_id, turn_id, native_id, quiescent)
                VALUES (@SessionId, @TurnId, @NativeId, @Quiescent)
                ON CONFLICT(session_id) DO UPDATE SET quiescent = excluded.quiescent
                WHERE provider_session_resume.turn_id = excluded.turn_id
                  AND provider_session_resume.native_id = excluded.native_id;
                """, fence, tx, cancellationToken: token)).ConfigureAwait(false));
        }, ct);

    public Task CompleteSessionResumeAsync(AgentSessionDto session, PersistedSessionInput? unresolved,
        EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            RequireOne(await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM provider_session_resume WHERE session_id = @SessionId AND quiescent = 1;",
                new { SessionId = session.Id }, tx, cancellationToken: token)).ConfigureAwait(false));
            if (unresolved is not null)
                await MarkSessionInputResolvedAsync(conn, tx, unresolved, session.UpdatedAtMs, token).ConfigureAwait(false);
            await UpsertSessionInternalAsync(conn, tx, session, token).ConfigureAwait(false);
            await InsertEventInternalAsync(conn, tx, sessionEvent, token).ConfigureAwait(false);
        }, ct);
}
