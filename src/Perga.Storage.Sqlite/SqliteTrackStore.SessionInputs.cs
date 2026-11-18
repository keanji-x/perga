using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Events;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore : ISessionInputStore
{
    private const string OwnerAuthor = "owner:installation";
    private const string InputSelect = """
        SELECT i.id AS InputId, i.turn_id AS TurnId, i.session_id AS SessionId,
               i.delivery_state AS DeliveryState, i.resolved_at_ms AS ResolvedAtMs, t.status AS TurnStatus,
               i.request_hash AS RequestHash, i.content AS Content, t.turn_index AS TurnIndex,
               t.epoch AS Epoch, t.provider AS Provider, t.model AS Model, s.track_id AS TrackId,
               track.area_id AS AreaId, t.started_at_ms AS StartedAtMs
        FROM session_inputs i
        JOIN session_turns t ON t.session_id = i.session_id AND t.id = i.turn_id
        JOIN agent_sessions s ON s.id = i.session_id
        JOIN tracks track ON track.id = s.track_id
        """;

    public ISessionInputStore SessionInputs => this;

    public async Task<bool> HasSessionTurnAsync(string sessionId, string turnId, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM session_turns WHERE session_id = @SessionId AND id = @TurnId);",
            new { SessionId = sessionId, TurnId = turnId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<int> GetLastTurnIndexAsync(string sessionId, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await ReadLastTurnIndexAsync(conn, null, sessionId, ct).ConfigureAwait(false);
    }

    private static Task<int> ReadLastTurnIndexAsync(SqliteConnection conn, SqliteTransaction? tx, string sessionId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(turn_index), 0) FROM session_turns WHERE session_id = @SessionId;",
            new { SessionId = sessionId }, tx, cancellationToken: ct));

    public Task<PersistedSessionInput?> FindByKeyAsync(string sessionId, string key, CancellationToken ct) =>
        FindInputAsync("WHERE i.session_id = @SessionId AND i.author_id = @Author AND i.request_key = @Key",
            new { SessionId = sessionId, Author = OwnerAuthor, Key = key }, ct);

    public Task<PersistedSessionInput?> FindByIdAsync(string sessionId, string inputId, CancellationToken ct) =>
        FindInputAsync("WHERE i.session_id = @SessionId AND i.author_id = @Author AND i.id = @InputId",
            new { SessionId = sessionId, Author = OwnerAuthor, InputId = inputId }, ct);

    public Task<PersistedSessionInput?> FindUnresolvedAsync(string sessionId, CancellationToken ct) =>
        FindInputAsync("""
            WHERE i.session_id = @SessionId AND i.author_id = @Author AND i.resolved_at_ms IS NULL
              AND t.status = 'unknown' ORDER BY t.turn_index DESC LIMIT 1
            """, new { SessionId = sessionId, Author = OwnerAuthor }, ct);

    private async Task<PersistedSessionInput?> FindInputAsync(string filter, object parameters, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<InputRow>(
            new CommandDefinition(InputSelect + "\n" + filter, parameters, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToInput();
    }

    public async Task<StoredSessionCreation?> FindCreationAsync(SessionCreateIntent intent, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var receipt = await conn.QuerySingleOrDefaultAsync<CreationRow>(new CommandDefinition(
            """
            SELECT resource_id AS ResourceId, request_hash AS RequestHash, first_input_id AS FirstInputId
            FROM creation_receipts
            WHERE author_id = @Author AND operation = 'session' AND scope_id = @TrackId AND request_key = @Key;
            """, new { Author = OwnerAuthor, intent.TrackId, intent.Key }, cancellationToken: ct)).ConfigureAwait(false);
        if (receipt is null) return null;
        var snapshot = await LoadTrackAggregateAsync(intent.TrackId, ct).ConfigureAwait(false);
        var session = snapshot?.Sessions.FirstOrDefault(candidate => candidate.Id == receipt.ResourceId);
        var input = session is not null && receipt.FirstInputId is { } id
            ? await FindByIdAsync(session.Id, id, ct).ConfigureAwait(false)
            : null;
        return new StoredSessionCreation(receipt.RequestHash, session, input);
    }

    public Task<PersistedSessionInput> AcceptAsync(
        AgentSessionDto session, SessionInputRequest request, EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            var reserved = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE agent_sessions SET status = 'queued', updated_at_ms = @UpdatedAtMs
                WHERE id = @Id AND status NOT IN ('queued', 'running', 'unknown');
                """, new { session.Id, session.UpdatedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
            if (reserved != 1)
                throw Perga.Kernel.KernelException.Conflict("The session already has an active or unresolved turn.");
            var input = await InsertInputAsync(conn, tx, session, request, innerCt).ConfigureAwait(false);
            await InsertEventInternalAsync(conn, tx, sessionEvent, innerCt).ConfigureAwait(false);
            return input;
        }, ct);

    public Task<PersistedSessionInput?> CreateAsync(
        SessionCreateIntent intent, AgentSessionDto session, SessionInputRequest? firstInput,
        EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            await UpsertSessionInternalAsync(conn, tx, session, innerCt).ConfigureAwait(false);
            var input = firstInput is null ? null
                : await InsertInputAsync(conn, tx, session, firstInput, innerCt).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO creation_receipts (author_id, operation, scope_id, request_key, request_hash,
                                               resource_id, first_input_id, created_at_ms)
                VALUES (@Author, 'session', @TrackId, @Key, @Hash, @Id, @InputId, @CreatedAtMs);
                """, new { Author = OwnerAuthor, intent.TrackId, intent.Key, intent.Hash, session.Id,
                    InputId = input?.Receipt.InputId, session.CreatedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
            await InsertEventInternalAsync(conn, tx, sessionEvent, innerCt).ConfigureAwait(false);
            return input;
        }, ct);

    private static async Task<PersistedSessionInput> InsertInputAsync(
        SqliteConnection conn, SqliteTransaction tx, AgentSessionDto session, SessionInputRequest request, CancellationToken ct)
    {
        var turnIndex = checked(await ReadLastTurnIndexAsync(conn, tx, session.Id, ct).ConfigureAwait(false) + 1);
        var turnId = $"trn_{turnIndex:D6}_{Guid.NewGuid():N}";
        var inputId = $"inp_{Guid.NewGuid():N}";
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO session_turns (id, session_id, turn_index, epoch, trigger_kind, status, accepted_at_ms, provider, model)
            VALUES (@TurnId, @SessionId, @TurnIndex, @Epoch, 'user', 'queued', @Now, @Provider, @Model);
            INSERT INTO session_inputs (id, session_id, turn_id, author_id, request_key, request_hash, content, accepted_at_ms, delivery_state)
            VALUES (@InputId, @SessionId, @TurnId, @Author, @Key, @Hash, @Content, @Now, 'accepted');
            INSERT INTO session_items (session_id, turn_id, kind, payload_json, created_at_ms, accepted_input_id)
            VALUES (@SessionId, @TurnId, 'user', '{}', @Now, @InputId);
            """, new { TurnId = turnId, InputId = inputId, SessionId = session.Id, TurnIndex = turnIndex,
                session.Epoch, Now = session.UpdatedAtMs, session.Provider, session.Model,
                Author = OwnerAuthor, request.Key, request.Hash, request.Content }, tx, cancellationToken: ct)).ConfigureAwait(false);

        return new PersistedSessionInput(new(inputId, turnId, session.Id, "accepted", "queued", null),
            request.Hash, request.Content, turnIndex, session.Epoch, session.Provider, session.Model);
    }

    public Task<bool> ClaimAsync(
        PersistedSessionInput input, AgentSessionDto session, EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            var updated = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE session_inputs SET delivery_state = 'dispatching', dispatch_started_at_ms = @Now
                WHERE id = @InputId AND delivery_state = 'accepted';
                """, new { input.Receipt.InputId, Now = session.UpdatedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
            if (updated == 0) return false;
            RequireOne(updated);
            RequireOne(await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE session_turns SET status = 'running', started_at_ms = @Now WHERE id = @TurnId AND status = 'queued';",
                new { input.Receipt.TurnId, Now = session.UpdatedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false));
            await UpsertSessionInternalAsync(conn, tx, session, innerCt).ConfigureAwait(false);
            await InsertEventInternalAsync(conn, tx, sessionEvent, innerCt).ConfigureAwait(false);
            return true;
        }, ct);

    public Task MarkDeliveredAsync(PersistedSessionInput input, string? nativeThreadId, string? providerTurnId, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            RequireOne(await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE session_inputs SET delivery_state = 'delivered'
                WHERE id = @InputId AND delivery_state IN ('dispatching', 'delivered');
                """, new { input.Receipt.InputId }, tx, cancellationToken: innerCt)).ConfigureAwait(false));
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE agent_sessions SET native_thread_id = COALESCE(@Thread, native_thread_id) WHERE id = @SessionId;
                UPDATE session_turns SET provider_turn_ref = COALESCE(@ProviderTurn, provider_turn_ref) WHERE id = @TurnId;
                """, new { input.Receipt.SessionId, input.Receipt.TurnId, Thread = nativeThreadId,
                    ProviderTurn = providerTurnId }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
        }, ct);

    public Task SettleAsync(
        PersistedSessionInput input, string delivery, SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
        AgentSessionDto session, IReadOnlyList<EventEnvelopeDto> events, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            RequireOne(await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE session_inputs SET delivery_state = @Delivery WHERE id = @InputId AND delivery_state IN ('dispatching', 'delivered');",
                new { Delivery = delivery, input.Receipt.InputId }, tx, cancellationToken: innerCt)).ConfigureAwait(false));
            RequireOne(await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE session_turns SET status = @Status, usage_json = @UsageJson, error_text = @ErrorText,
                    completed_at_ms = @CompletedAtMs WHERE id = @Id AND status = 'running';
                """, new { turn.Id, turn.Status, UsageJson = SqliteJsonUpcaster.SerializeNullableElement(turn.Usage),
                    turn.ErrorText, turn.CompletedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false));
            await InsertSessionItemsAsync(conn, tx, session.TrackId, items, innerCt).ConfigureAwait(false);
            await UpsertSessionInternalAsync(conn, tx, session, innerCt).ConfigureAwait(false);
            foreach (var evt in events)
                await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
        }, ct);

    public Task ResolveAsync(
        PersistedSessionInput input, AgentSessionDto session, EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            RequireOne(await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE session_inputs SET resolved_at_ms = @Now
                WHERE id = @InputId AND resolved_at_ms IS NULL
                  AND EXISTS (SELECT 1 FROM session_turns WHERE id = turn_id AND status = 'unknown');
                """, new { input.Receipt.InputId, Now = session.UpdatedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false));
            await UpsertSessionInternalAsync(conn, tx, session, innerCt).ConfigureAwait(false);
            await InsertEventInternalAsync(conn, tx, sessionEvent, innerCt).ConfigureAwait(false);
        }, ct);

    public Task<IReadOnlyList<(string TrackId, PersistedSessionInput Input)>> RecoverSessionInputsAsync(CancellationToken ct) =>
        _writer.EnqueueAsync<IReadOnlyList<(string, PersistedSessionInput)>>(async (conn, tx, innerCt) =>
        {
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var interrupted = await conn.QueryAsync<InputRow>(new CommandDefinition(
                InputSelect + "\nWHERE t.status = 'running';", transaction: tx, cancellationToken: innerCt)).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE agent_sessions SET status = 'unknown', updated_at_ms = @Now
                WHERE id IN (
                    SELECT i.session_id FROM session_inputs i JOIN session_turns t ON t.id = i.turn_id
                    WHERE t.status = 'running');
                UPDATE session_turns SET status = 'unknown', error_text = 'Host stopped during dispatch.'
                WHERE status = 'running' AND id IN (SELECT turn_id FROM session_inputs);
                UPDATE session_inputs SET delivery_state = 'unknown' WHERE delivery_state = 'dispatching';
                """, new { Now = now }, tx, cancellationToken: innerCt)).ConfigureAwait(false);

            foreach (var row in interrupted)
            {
                var turn = new SessionTurnDto(row.TurnId, row.SessionId, row.TurnIndex, row.Epoch,
                    "user", JsonSerializer.SerializeToElement(new { }), "unknown", null,
                    "Host stopped during dispatch.", row.StartedAtMs, null);
                var payload = JsonSerializer.SerializeToElement(turn, JsonSerializerOptions.Web);
                await InsertEventInternalAsync(conn, tx,
                    new(null, row.AreaId, row.TrackId, KernelEventKinds.TurnCompleted, payload, now), innerCt)
                    .ConfigureAwait(false);
            }

            var queued = await conn.QueryAsync<InputRow>(new CommandDefinition(
                InputSelect + "\nWHERE i.delivery_state = 'accepted' ORDER BY i.accepted_at_ms, i.id;",
                transaction: tx, cancellationToken: innerCt)).ConfigureAwait(false);
            return queued.Select(row => (row.TrackId, row.ToInput())).ToArray();
        }, ct);

    private static void RequireOne(int updated)
    {
        if (updated != 1)
            throw new InvalidOperationException("The persisted session input changed unexpectedly.");
    }

    private sealed record CreationRow(string ResourceId, string RequestHash, string? FirstInputId);

    private sealed class InputRow
    {
        public string InputId { get; init; } = "";
        public string TurnId { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string TrackId { get; init; } = "";
        public string AreaId { get; init; } = "";
        public long? StartedAtMs { get; init; }
        public string DeliveryState { get; init; } = "";
        public string TurnStatus { get; init; } = "";
        public long? ResolvedAtMs { get; init; }
        public string RequestHash { get; init; } = "";
        public string Content { get; init; } = "";
        public int TurnIndex { get; init; }
        public int Epoch { get; init; }
        public string Provider { get; init; } = "";
        public string? Model { get; init; }

        public PersistedSessionInput ToInput() => new(
            new(InputId, TurnId, SessionId, DeliveryState, TurnStatus, ResolvedAtMs),
            RequestHash, Content, TurnIndex, Epoch, Provider, Model);
    }
}
