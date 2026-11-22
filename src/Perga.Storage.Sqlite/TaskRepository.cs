using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Task persistence, diagnostics, session boundaries and settlement wakes.</summary>
internal sealed class TaskRepository
{
    private readonly SqliteStoreContext _context;
    private readonly SessionRepository _sessionRepository;

    internal TaskRepository(SqliteStoreContext context, SessionRepository sessionRepository)
    {
        _context = context;
        _sessionRepository = sessionRepository;
    }

    public Task UpsertTaskAsync(
        TaskDto task,
        EventEnvelopeDto? evt,
        CancellationToken ct,
        AgentSessionDto? session = null,
        EventEnvelopeDto? sessionEvent = null,
        string? wakePrompt = null,
        SessionMessage? initialMessage = null,
        bool writeNewRunBoundary = false,
        bool suppressDefaultWake = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        _context.EnsureInitialized();

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                if (session is not null)
                {
                    await SessionRepository.UpsertSessionInternalAsync(conn, tx, session, innerCt).ConfigureAwait(false);
                    if (writeNewRunBoundary)
                    {
                        await _sessionRepository.InsertSessionItemsAsync(conn, tx, session.TrackId, [new SessionItemDto(0, session.Id, null,
                            "epoch_boundary", System.Text.Json.JsonSerializer.SerializeToElement(new { epoch = session.Epoch, reason = "new_run" }), session.UpdatedAtMs)], innerCt).ConfigureAwait(false);
                    }
                }

                await UpsertTaskInternalAsync(conn, tx, task, innerCt).ConfigureAwait(false);

                if (sessionEvent is not null)
                    await EventRepository.InsertEventInternalAsync(conn, tx, sessionEvent, innerCt).ConfigureAwait(false);
                if (initialMessage is not null)
                    await SessionMessageSql.InsertSessionMessageAsync(conn, tx, initialMessage, innerCt).ConfigureAwait(false);

                if (task.FinishedAtMs is not null && task.SessionId is not null)
                {
                    await conn.ExecuteAsync(new CommandDefinition("""
                        UPDATE session_messages SET delivery = 'interrupted'
                        WHERE session_id = @SessionId AND delivery = 'queued';
                        """, new { task.SessionId }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                }

                if (task.FinishedAtMs is not null && !suppressDefaultWake)
                {
                    var child = task.SessionId is null ? null : await conn.QuerySingleOrDefaultAsync<(string? Parent, string Notify)?>(new CommandDefinition(
                        "SELECT parent_session_id, notify_parent FROM agent_sessions WHERE id=@Id;", new { Id = task.SessionId }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                    if (child?.Parent is { } parentId && child.Value.Notify == "task_settled" &&
                        await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                            "SELECT EXISTS(SELECT 1 FROM agent_sessions WHERE id=@Id AND archived_at_ms IS NULL);", new { Id = parentId }, tx, cancellationToken: innerCt)).ConfigureAwait(false))
                        await TrackWakesRepository.EnqueueWakeOnceInternalAsync(conn, tx, TrackWakeRequests.TaskSettled(task, wakePrompt) with { TargetSessionId = parentId }, task.UpdatedAtMs, innerCt).ConfigureAwait(false);
                    else if (child is null || child.Value.Parent is null)
                        await TrackWakesRepository.EnqueueWakeOnceInternalAsync(conn, tx, TrackWakeRequests.TaskSettled(task, wakePrompt), task.UpdatedAtMs, innerCt).ConfigureAwait(false);
                }
                if (evt is not null)
                {
                    await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }

    internal async Task UpsertTaskInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        TaskDto task,
        CancellationToken ct)
    {
        // Guard against an updated Id replacing an existing (track_id, task_key) row while preserving normal in-place Id updates.
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM tasks WHERE track_id = @TrackId AND task_key = @TaskKey AND id != @Id;",
                new { task.TrackId, task.TaskKey, task.Id },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO tasks (
                    id, track_id, block_id, task_key, generation, kind, provider, role,
                    access_mode, ready, depends_on_json, goal_or_cmd, context_json,
                    acceptance, gate_json, status, status_detail, semantic_verdict,
                    semantic_note, session_id, run_count, worker_result_json, meta_json,
                    gate_verdict_json, started_at_ms, finished_at_ms,
                    outcome, outcome_summary, trigger_run_key, archived_at_ms,
                    created_at_ms, updated_at_ms
                )
                VALUES (
                    @Id, @TrackId, @BlockId, @TaskKey, @Generation, @Kind, @Provider, @Role,
                    @AccessMode, @Ready, @DependsOnJson, @GoalOrCmd, @ContextJson,
                    @Acceptance, @GateJson, @Status, @StatusDetail, @SemanticVerdict,
                    @SemanticNote, @SessionId, @RunCount, @WorkerResultJson, @MetaJson,
                    @GateVerdictJson, @StartedAtMs, @FinishedAtMs,
                    @Outcome, @OutcomeSummary, @TriggerRunKey, @ArchivedAtMs,
                    @CreatedAtMs, @UpdatedAtMs
                )
                ON CONFLICT(id) DO UPDATE SET
                    block_id = excluded.block_id,
                    task_key = excluded.task_key,
                    generation = excluded.generation,
                    kind = excluded.kind,
                    provider = excluded.provider,
                    role = excluded.role,
                    access_mode = excluded.access_mode,
                    ready = excluded.ready,
                    depends_on_json = excluded.depends_on_json,
                    goal_or_cmd = excluded.goal_or_cmd,
                    context_json = excluded.context_json,
                    acceptance = excluded.acceptance,
                    gate_json = excluded.gate_json,
                    status = excluded.status,
                    status_detail = excluded.status_detail,
                    semantic_verdict = excluded.semantic_verdict,
                    semantic_note = excluded.semantic_note,
                    session_id = excluded.session_id,
                    run_count = excluded.run_count,
                    worker_result_json = excluded.worker_result_json,
                    meta_json = excluded.meta_json,
                    gate_verdict_json = excluded.gate_verdict_json,
                    started_at_ms = excluded.started_at_ms,
                    finished_at_ms = excluded.finished_at_ms,
                    outcome = excluded.outcome,
                    outcome_summary = excluded.outcome_summary,
                    trigger_run_key = excluded.trigger_run_key,
                    archived_at_ms = excluded.archived_at_ms,
                    updated_at_ms = excluded.updated_at_ms;
                """,
                new
                {
                    task.Id,
                    task.TrackId,
                    task.BlockId,
                    task.TaskKey,
                    task.Generation,
                    task.Kind,
                    task.Provider,
                    task.Role,
                    AccessMode = SqliteJsonUpcaster.FormatAccessMode(task.Access),
                    Ready = task.Ready ? 1 : 0,
                    DependsOnJson = SqliteJsonUpcaster.SerializeStringList(task.DependsOn),
                    task.GoalOrCmd,
                    ContextJson = SqliteJsonUpcaster.SerializeNullableElement(task.Context),
                    task.Acceptance,
                    GateJson = SqliteJsonUpcaster.SerializeGateSpec(task.Gate),
                    Status = SqliteJsonUpcaster.FormatTaskStatus(task.Status),
                    task.StatusDetail,
                    SemanticVerdict = SqliteJsonUpcaster.FormatSemanticVerdict(task.SemanticVerdict),
                    task.SemanticNote,
                    task.SessionId,
                    task.RunCount,
                    WorkerResultJson = SqliteJsonUpcaster.SerializeNullableElement(task.WorkerResult),
                    MetaJson = task.Meta.HasValue ? SqliteJsonUpcaster.SerializeElement(task.Meta.Value) : "{}",
                    GateVerdictJson = _context.ColdBlobs.StoreOrInlineNullable(task.TrackId, task.GateVerdict),
                    task.StartedAtMs,
                    task.FinishedAtMs,
                    Outcome = SqliteJsonUpcaster.FormatFlowOutcome(task.Outcome),
                    task.OutcomeSummary,
                    task.TriggerRunKey,
                    task.ArchivedAtMs,
                    task.CreatedAtMs,
                    task.UpdatedAtMs
                },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    internal TaskDto MapTask(TaskRow r) =>
        new(
            Id: r.Id,
            TrackId: r.TrackId,
            BlockId: r.BlockId,
            TaskKey: r.TaskKey,
            Generation: (int)r.Generation,
            Kind: r.Kind,
            Provider: r.Provider,
            Role: r.Role,
            Access: SqliteJsonUpcaster.ParseAccessMode(r.AccessMode),
            Ready: r.Ready != 0,
            DependsOn: SqliteJsonUpcaster.DeserializeStringList(r.DependsOnJson),
            GoalOrCmd: r.GoalOrCmd,
            Context: SqliteJsonUpcaster.ParseNullableElement(r.ContextJson),
            Acceptance: r.Acceptance,
            Gate: SqliteJsonUpcaster.DeserializeGateSpec(r.GateJson),
            Status: SqliteJsonUpcaster.ParseTaskStatus(r.Status),
            StatusDetail: r.StatusDetail,
            SemanticVerdict: SqliteJsonUpcaster.ParseSemanticVerdict(r.SemanticVerdict),
            SemanticNote: r.SemanticNote,
            SessionId: r.SessionId,
            RunCount: (int)r.RunCount,
            WorkerResult: SqliteJsonUpcaster.ParseNullableElement(r.WorkerResultJson),
            Meta: _context.Upcaster.UpcastObjectOrElement(r.MetaJson, "tasks.meta_json"),
            GateVerdict: _context.ColdBlobs.HydrateNullable(r.GateVerdictJson, "tasks.gate_verdict_json"),
            StartedAtMs: r.StartedAtMs,
            FinishedAtMs: r.FinishedAtMs,
            Outcome: SqliteJsonUpcaster.ParseFlowOutcome(r.Outcome),
            OutcomeSummary: r.OutcomeSummary,
            TriggerRunKey: r.TriggerRunKey,
            ArchivedAtMs: r.ArchivedAtMs,
            CreatedAtMs: r.CreatedAtMs,
            UpdatedAtMs: r.UpdatedAtMs);

    internal sealed class TaskRow
    {
        public string Id { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string BlockId { get; init; } = string.Empty;
        public string TaskKey { get; init; } = string.Empty;
        public long Generation { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string? Provider { get; init; }
        public string? Role { get; init; }
        public string AccessMode { get; init; } = "read_write";
        public long Ready { get; init; }
        public string DependsOnJson { get; init; } = "[]";
        public string GoalOrCmd { get; init; } = string.Empty;
        public string? ContextJson { get; init; }
        public string? Acceptance { get; init; }
        public string? GateJson { get; init; }
        public string Status { get; init; } = "pending";
        public string? StatusDetail { get; init; }
        public string SemanticVerdict { get; init; } = "none";
        public string? SemanticNote { get; init; }
        public string? SessionId { get; init; }
        public long RunCount { get; init; }
        public string? WorkerResultJson { get; init; }
        public string MetaJson { get; init; } = "{}";
        public string? GateVerdictJson { get; init; }
        public long? StartedAtMs { get; init; }
        public long? FinishedAtMs { get; init; }
        public string Outcome { get; init; } = "none";
        public string? OutcomeSummary { get; init; }
        public string? TriggerRunKey { get; init; }
        public long? ArchivedAtMs { get; init; }
        public long CreatedAtMs { get; init; }
        public long UpdatedAtMs { get; init; }
    }
}
