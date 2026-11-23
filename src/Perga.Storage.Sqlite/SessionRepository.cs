using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Sessions, turns, item histories and cold-payload hydration.</summary>
internal sealed class SessionRepository
{
    private readonly SqliteStoreContext _context;

    internal SessionRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public Task UpsertSessionAsync(AgentSessionDto session, EventEnvelopeDto? evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        _context.EnsureInitialized();

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await UpsertSessionInternalAsync(conn, tx, session, innerCt).ConfigureAwait(false);
                if (session.IsLead)
                {
                    await conn.ExecuteAsync(
                        new CommandDefinition(
                            "UPDATE tracks SET lead_session_id = @SessionId WHERE id = @TrackId AND lead_session_id IS NULL;",
                            new { SessionId = session.Id, session.TrackId },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);
                }

                if (evt is not null)
                {
                    await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }

    public async Task<string?> FindTrackIdBySessionAsync(string sessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _context.EnsureInitialized();

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT track_id FROM agent_sessions WHERE id = @SessionId;",
                new { SessionId = sessionId },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task RecordTurnAndItemsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct)
    {
        _ = await RecordTurnAndItemsWithIdsAsync(turn, newItems, updatedSession, ct).ConfigureAwait(false);
    }

    public Task<(SessionTurnDto Turn, IReadOnlyList<long> ItemIds)> RecordTurnAndItemsWithIdsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct = default, SessionMessage? dispatchMessage = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(newItems);
        _context.EnsureInitialized();

        var inputJson = SqliteJsonUpcaster.SerializeElement(turn.Input);
        var usageJson = SqliteJsonUpcaster.SerializeNullableElement(turn.Usage);

        return _context.Writer.EnqueueAsync<(SessionTurnDto, IReadOnlyList<long>)>(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO session_turns (
                            id, session_id, turn_index, epoch,
                            trigger_kind, input_json, status, usage_json, error_text,
                            started_at_ms, completed_at_ms
                        )
                        VALUES (
                            @Id, @SessionId, @TurnIndex, @Epoch,
                            @TriggerKind, @InputJson, @Status, @UsageJson, @ErrorText,
                            @StartedAtMs, @CompletedAtMs
                        )
                        ON CONFLICT(id) DO UPDATE SET
                            epoch = excluded.epoch,
                            trigger_kind = excluded.trigger_kind,
                            input_json = excluded.input_json,
                            status = excluded.status,
                            usage_json = excluded.usage_json,
                            error_text = excluded.error_text,
                            completed_at_ms = excluded.completed_at_ms;
                        """,
                        new
                        {
                            turn.Id,
                            turn.SessionId,
                            turn.TurnIndex,
                            turn.Epoch,
                            turn.TriggerKind,
                            InputJson = inputJson,
                            turn.Status,
                            UsageJson = usageJson,
                            turn.ErrorText,
                            turn.StartedAtMs,
                            turn.CompletedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                long? messageOrdinal = null;
                if (dispatchMessage is not null)
                {
                    if (!await SessionMessageSql.TransitionMessageAsync(conn, tx, dispatchMessage with { TurnId = turn.Id, Delivery = "dispatching" },
                        "dispatching", innerCt).ConfigureAwait(false))
                        throw new InvalidOperationException("The claimed message no longer belongs to this dispatch.");
                    messageOrdinal = await SessionMessageSql.ReadMessageOrdinalAsync(conn, tx, dispatchMessage, innerCt).ConfigureAwait(false);
                }

                if (updatedSession is not null)
                {
                    await UpsertSessionInternalAsync(conn, tx, updatedSession, innerCt).ConfigureAwait(false);
                }

                IReadOnlyList<long> insertedIds = [];
                long? userItemId = turn.UserItemId;
                if (newItems.Count > 0)
                {
                    var trackId = updatedSession?.TrackId ?? await conn.ExecuteScalarAsync<string?>(
                        new CommandDefinition(
                            "SELECT track_id FROM agent_sessions WHERE id = @SessionId;",
                            new { turn.SessionId },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);

                    insertedIds = await InsertSessionItemsAsync(conn, tx, trackId, newItems, innerCt, messageOrdinal).ConfigureAwait(false);
                    for (var i = 0; i < newItems.Count; i++)
                    {
                        if (userItemId is null && string.Equals(newItems[i].Kind, "user", StringComparison.Ordinal))
                        {
                            userItemId = insertedIds[i];
                            break;
                        }
                    }
                }

                userItemId ??= await conn.ExecuteScalarAsync<long?>(
                    new CommandDefinition(
                        "SELECT id FROM session_items WHERE turn_id = @TurnId AND kind = 'user' ORDER BY id ASC LIMIT 1;",
                        new { TurnId = turn.Id },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                var resolvedTurn = userItemId == turn.UserItemId ? turn : turn with { UserItemId = userItemId };
                return (resolvedTurn, insertedIds);
            },
            ct);
    }

    public Task<IReadOnlyList<long>> AppendSessionItemsAsync(
        string sessionId,
        IReadOnlyList<SessionItemDto> newItems,
        CancellationToken ct = default, EventEnvelopeDto? evt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(newItems);
        _context.EnsureInitialized();

        if (newItems.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        return _context.Writer.EnqueueAsync<IReadOnlyList<long>>(
            async (conn, tx, innerCt) =>
            {
                var trackId = await conn.ExecuteScalarAsync<string?>(
                    new CommandDefinition(
                        "SELECT track_id FROM agent_sessions WHERE id = @SessionId;",
                        new { SessionId = sessionId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                var ids = await InsertSessionItemsAsync(conn, tx, trackId, newItems, innerCt).ConfigureAwait(false);
                if (evt is not null)
                    await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                return ids;
            },
            ct);
    }

    internal async Task<IReadOnlyList<long>> InsertSessionItemsAsync(
        SqliteConnection conn, SqliteTransaction tx, string? trackId, IReadOnlyList<SessionItemDto> items, CancellationToken ct, long? messageOrdinal = null)
    {
        var ids = new List<long>(items.Count);
        foreach (var item in items)
        {
            var payloadJson = !string.IsNullOrWhiteSpace(trackId)
                ? _context.ColdBlobs.StoreOrInline(trackId, item.Payload)
                : SqliteJsonUpcaster.SerializeElement(item.Payload);

            var insertedId = await conn.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    """
                    INSERT INTO session_items (session_id, turn_id, kind, payload_json, created_at_ms, message_ordinal)
                    VALUES (@SessionId, @TurnId, @Kind, @PayloadJson, @CreatedAtMs, @MessageOrdinal);
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        item.SessionId,
                        item.TurnId,
                        item.Kind,
                        PayloadJson = payloadJson,
                        item.CreatedAtMs,
                        MessageOrdinal = item.Kind == "user" ? messageOrdinal : null
                    },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            ids.Add(insertedId);
        }

        return ids;
    }

    public async Task<IReadOnlyList<SessionTurnDto>> ListSessionTurnsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _context.EnsureInitialized();

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SessionTurnRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       session_id AS SessionId,
                       turn_index AS TurnIndex,
                       epoch AS Epoch,
                       trigger_kind AS TriggerKind,
                       input_json AS InputJson,
                       status AS Status,
                       usage_json AS UsageJson,
                       error_text AS ErrorText,
                       started_at_ms AS StartedAtMs,
                       completed_at_ms AS CompletedAtMs
                FROM session_turns
                WHERE session_id = @SessionId
                ORDER BY turn_index ASC, started_at_ms ASC;
                """,
                new { SessionId = sessionId },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapTurn).ToList();
    }

    public async Task<IReadOnlyList<SessionItemDto>> ListSessionItemsAsync(
        string sessionId,
        long afterId = 0,
        int limit = 100,
        CancellationToken ct = default, bool latest = false, long? beforeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _context.EnsureInitialized();

        var reverse = latest || beforeId is not null;
        var clampedLimit = Math.Max(1, limit);
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SessionItemRow>(
            new CommandDefinition(
                $"""
                SELECT item.id AS Id,
                       item.session_id AS SessionId,
                       item.turn_id AS TurnId,
                       item.kind AS Kind,
                       CASE WHEN input.id IS NULL OR input.kind = 'worker' OR input.turn_id IS NULL THEN item.payload_json
                            ELSE json_object('v', 1, 'content', input.content, 'triggerKind', 'user', 'inputId', input.id)
                       END AS PayloadJson,
                       item.created_at_ms AS CreatedAtMs,
                       CASE WHEN item.turn_id IS NULL AND json_extract(item.payload_json,'$.archived') = 1 THEN json_extract(item.payload_json,'$.status') ELSE turn.status END AS TurnStatus,
                       CASE WHEN input.delivery = 'queued' THEN 'accepted' ELSE input.delivery END AS InputDelivery
                FROM session_items item
                LEFT JOIN session_messages input ON input.ordinal = item.message_ordinal
                LEFT JOIN session_turns turn ON turn.session_id = item.session_id AND turn.id = item.turn_id
                WHERE item.session_id = @SessionId
                  AND item.id > @AfterId AND (@BeforeId IS NULL OR item.id < @BeforeId)
                ORDER BY item.id {(reverse ? "DESC" : "ASC")}
                LIMIT @Limit;
                """,
                new
                {
                    SessionId = sessionId,
                    AfterId = afterId,
                    BeforeId = beforeId,
                    Limit = clampedLimit
                },
                cancellationToken: ct)).ConfigureAwait(false);

        return (reverse ? rows.Reverse() : rows).Select(MapSessionItem).ToList();
    }

    internal static Task UpsertSessionInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        AgentSessionDto session,
        CancellationToken ct)
    {
        return conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO agent_sessions (
                    id, track_id, title,
                    provider, model, reasoning_effort, native_thread_id, epoch, status,
                    context_used_tokens, context_window_tokens, context_usage_at_ms,
                    archived_at_ms, created_at_ms, updated_at_ms, permission_mode,
                    unknown_reason, unknown_turn_id, unknown_last_error, reconnect_invalidated,
                    parent_session_id, spawn_depth, config_version, notify_parent, system_prompt, source_session_id
                )
                VALUES (
                    @Id, @TrackId, @Title,
                    @Provider, @Model, @ReasoningEffort, @NativeThreadId, @Epoch, @Status,
                    @ContextUsedTokens, @ContextWindowTokens, @ContextUsageAtMs,
                    @ArchivedAtMs, @CreatedAtMs, @UpdatedAtMs, @PermissionMode,
                    @UnknownReason, @UnknownTurnId, @UnknownLastError, @ReconnectInvalidated,
                    @ParentSessionId, @SpawnDepth, @ConfigVersion, @NotifyParent, @SystemPrompt, @SourceSessionId
                )
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    provider = excluded.provider,
                    model = excluded.model,
                    reasoning_effort = excluded.reasoning_effort,
                    native_thread_id = excluded.native_thread_id,
                    epoch = excluded.epoch,
                    status = excluded.status,
                    context_used_tokens = excluded.context_used_tokens,
                    context_window_tokens = excluded.context_window_tokens,
                    context_usage_at_ms = excluded.context_usage_at_ms,
                    archived_at_ms = excluded.archived_at_ms,
                    updated_at_ms = excluded.updated_at_ms,
                    permission_mode = excluded.permission_mode,
                    unknown_reason = excluded.unknown_reason, unknown_turn_id = excluded.unknown_turn_id,
                    unknown_last_error = excluded.unknown_last_error, reconnect_invalidated = excluded.reconnect_invalidated,
                    parent_session_id = excluded.parent_session_id, spawn_depth = excluded.spawn_depth,
                    config_version = excluded.config_version, notify_parent = excluded.notify_parent, system_prompt = excluded.system_prompt;
                """,
                new
                {
                    session.Id,
                    session.TrackId,
                    session.Title,
                    session.Provider,
                    session.Model,
                    session.ReasoningEffort,
                    session.NativeThreadId,
                    session.Epoch,
                    session.Status,
                    ContextUsedTokens = session.ContextUsage?.UsedTokens,
                    ContextWindowTokens = session.ContextUsage?.WindowTokens,
                    ContextUsageAtMs = session.ContextUsage?.ObservedAtMs,
                    session.ArchivedAtMs,
                    session.CreatedAtMs,
                    session.UpdatedAtMs,
                    session.PermissionMode, session.UnknownReason, session.UnknownTurnId,
                    session.UnknownLastError, session.ReconnectInvalidated, session.ParentSessionId,
                    session.SpawnDepth, session.ConfigVersion, session.NotifyParent, session.SystemPrompt, session.SourceSessionId
                },
                transaction: tx,
                cancellationToken: ct));
    }

    internal const string SessionColumns = """
        id AS Id,
        track_id AS TrackId,
        title AS Title,
        provider AS Provider,
        model AS Model,
        reasoning_effort AS ReasoningEffort,
        native_thread_id AS NativeThreadId,
        epoch AS Epoch,
        status AS Status,
        context_used_tokens AS ContextUsedTokens,
        context_window_tokens AS ContextWindowTokens,
        context_usage_at_ms AS ContextUsageAtMs,
        archived_at_ms AS ArchivedAtMs,
        created_at_ms AS CreatedAtMs,
        updated_at_ms AS UpdatedAtMs,
        permission_mode AS PermissionMode,
        unknown_reason AS UnknownReason, unknown_turn_id AS UnknownTurnId,
        (SELECT MAX(t.completed_at_ms) FROM session_turns t WHERE t.session_id = agent_sessions.id
         AND LOWER(t.status) IN ('completed', 'failed') AND t.completed_at_ms IS NOT NULL) AS LastNotifiableTurnAtMs,
        unknown_last_error AS UnknownLastError, reconnect_invalidated AS ReconnectInvalidated,
        parent_session_id AS ParentSessionId, spawn_depth AS SpawnDepth,
        config_version AS ConfigVersion, notify_parent AS NotifyParent, system_prompt AS SystemPrompt,
        source_session_id AS SourceSessionId
        """;

    internal static AgentSessionDto MapSession(SessionRow r, string? leadSessionId) =>
        new(
            Id: r.Id,
            TrackId: r.TrackId,
            IsLead: !string.IsNullOrWhiteSpace(leadSessionId) &&
                    string.Equals(r.Id, leadSessionId, StringComparison.Ordinal),
            Title: r.Title,
            Provider: r.Provider,
            Model: r.Model,
            NativeThreadId: r.NativeThreadId,
            Epoch: (int)r.Epoch,
            Status: r.Status,
            ContextUsage: r.ContextUsedTokens is { } used && r.ContextUsageAtMs is { } observedAt
                ? new SessionContextUsage(used, r.ContextWindowTokens, observedAt)
                : null,
            ArchivedAtMs: r.ArchivedAtMs,
            CreatedAtMs: r.CreatedAtMs,
            UpdatedAtMs: r.UpdatedAtMs,
            ReasoningEffort: r.ReasoningEffort,
            PermissionMode: r.PermissionMode, UnknownReason: r.UnknownReason, UnknownTurnId: r.UnknownTurnId,
            UnknownLastError: r.UnknownLastError, ReconnectInvalidated: r.ReconnectInvalidated,
            ParentSessionId: r.ParentSessionId, SpawnDepth: r.SpawnDepth, ConfigVersion: r.ConfigVersion,
            NotifyParent: r.NotifyParent, SystemPrompt: r.SystemPrompt, LastNotifiableTurnAtMs: r.LastNotifiableTurnAtMs,
            SourceSessionId: r.SourceSessionId);

    private static SessionTurnDto MapTurn(SessionTurnRow r) =>
        new(
            Id: r.Id,
            SessionId: r.SessionId,
            TurnIndex: (int)r.TurnIndex,
            Epoch: (int)r.Epoch,
            TriggerKind: r.TriggerKind,
            Input: SqliteJsonUpcaster.ParseElementOrDefault(r.InputJson),
            Status: r.Status,
            Usage: SqliteJsonUpcaster.ParseNullableElement(r.UsageJson),
            ErrorText: r.ErrorText,
            StartedAtMs: r.StartedAtMs,
            CompletedAtMs: r.CompletedAtMs);

    private SessionItemDto MapSessionItem(SessionItemRow r) =>
        new(
            Id: r.Id,
            SessionId: r.SessionId,
            TurnId: r.TurnId,
            Kind: r.Kind,
            Payload: _context.ColdBlobs.Hydrate(r.PayloadJson, "session_items.payload_json"),
            CreatedAtMs: r.CreatedAtMs) { TurnStatus = r.TurnStatus, InputDelivery = r.InputDelivery };

    internal sealed class SessionRow
    {
        public long? LastNotifiableTurnAtMs { get; init; }
        public string? ParentSessionId { get; init; }
        public string? SourceSessionId { get; init; }
        public int SpawnDepth { get; init; }
        public long ConfigVersion { get; init; }
        public string NotifyParent { get; init; } = "none";
        public string? SystemPrompt { get; init; }
        public string? UnknownReason { get; init; }
        public string? UnknownTurnId { get; init; }
        public string? UnknownLastError { get; init; }
        public bool ReconnectInvalidated { get; init; }
        public string Id { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string? Title { get; init; }
        public string Provider { get; init; } = string.Empty;
        public string? Model { get; init; }
        public string? ReasoningEffort { get; init; }
        public string? NativeThreadId { get; init; }
        public long Epoch { get; init; }
        public string Status { get; init; } = "idle";
        public long? ContextUsedTokens { get; init; }
        public long? ContextWindowTokens { get; init; }
        public long? ContextUsageAtMs { get; init; }
        public long? ArchivedAtMs { get; init; }
        public long CreatedAtMs { get; init; }
        public long UpdatedAtMs { get; init; }
        public string? PermissionMode { get; init; }
    }

    private sealed class SessionTurnRow
    {
        public string Id { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public long TurnIndex { get; init; }
        public long Epoch { get; init; }
        public string TriggerKind { get; init; } = string.Empty;
        public string InputJson { get; init; } = "{}";
        public string Status { get; init; } = string.Empty;
        public string? UsageJson { get; init; }
        public string? ErrorText { get; init; }
        public long? StartedAtMs { get; init; }
        public long? CompletedAtMs { get; init; }
    }

    internal sealed class SessionItemRow
    {
        public long Id { get; init; }
        public string SessionId { get; init; } = string.Empty;
        public string? TurnId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = "{}";
        public long CreatedAtMs { get; init; }
        public string? TurnStatus { get; init; }
        public string? InputDelivery { get; init; }
    }
}
