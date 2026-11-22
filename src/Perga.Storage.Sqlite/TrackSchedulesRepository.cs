using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Kernel.Events;

namespace Perga.Storage.Sqlite;

/// <summary>Flow clock persistence and atomic firing with tasks and events.</summary>
internal sealed class TrackSchedulesRepository
{
    private readonly SqliteStoreContext _context;
    private readonly TaskRepository _taskRepository;

    internal TrackSchedulesRepository(SqliteStoreContext context, TaskRepository taskRepository)
    {
        _context = context;
        _taskRepository = taskRepository;
    }

    private const string ScheduleSelect = """
        SELECT id AS Id, track_id AS TrackId,
               source_kind AS SourceKind, schedule_kind AS ScheduleKind, title AS Title,
               prompt AS Prompt, cron_expr AS CronExpr, timezone AS Timezone,
               run_at_ms AS RunAtMs, next_fire_at_ms AS NextFireAtMs, last_fired_at_ms AS LastFiredAtMs,
               max_occurrences AS MaxOccurrences, fired_count AS FiredCount,
               state AS State, created_at_ms AS CreatedAtMs, updated_at_ms AS UpdatedAtMs,
               node_key AS NodeKey
        FROM track_schedules
        """;

    internal static async Task UpsertFlowScheduleInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        TrackScheduleDto schedule,
        CancellationToken ct,
        bool resetProgress = false)
    {
        var existing = await conn.QuerySingleOrDefaultAsync<TrackScheduleRow>(new CommandDefinition(
            ScheduleSelect + " WHERE track_id=@TrackId AND id=@Id;",
            new { schedule.TrackId, schedule.Id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (existing is { NodeKey: null })
            throw KernelException.Conflict($"Schedule ID '{schedule.Id}' belongs to a standalone schedule.");
        var keepProgress = !resetProgress && existing is not null && existing.State != TrackScheduleStates.Canceled &&
            existing.ScheduleKind == schedule.ScheduleKind &&
            existing.CronExpr == schedule.CronExpr &&
            existing.Timezone == schedule.Timezone &&
            existing.RunAtMs == schedule.RunAtMs;
        var nextFire = keepProgress
            ? schedule.MaxOccurrences is { } cap && existing!.FiredCount >= cap
                ? null : existing!.NextFireAtMs ?? schedule.NextFireAtMs
            : schedule.NextFireAtMs;
        var toPersist = keepProgress
            ? schedule with
            {
                State = nextFire is null ? TrackScheduleStates.Done : TrackScheduleStates.Active,
                NextFireAtMs = nextFire,
                LastFiredAtMs = existing!.LastFiredAtMs,
                FiredCount = checked((int)existing.FiredCount),
                CreatedAtMs = existing.CreatedAtMs
            }
            : schedule with { CreatedAtMs = existing?.CreatedAtMs ?? schedule.CreatedAtMs };

        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO track_schedules(
                id, track_id, source_kind, schedule_kind, title, prompt,
                cron_expr, timezone, run_at_ms, next_fire_at_ms, last_fired_at_ms,
                max_occurrences, fired_count, state, created_at_ms, updated_at_ms, node_key)
            VALUES(
                @Id, @TrackId, @SourceKind, @ScheduleKind, @Title, @Prompt,
                @CronExpr, @Timezone, @RunAtMs, @NextFireAtMs, @LastFiredAtMs,
                @MaxOccurrences, @FiredCount, @State, @CreatedAtMs, @UpdatedAtMs, @NodeKey)
            ON CONFLICT(track_id, id) DO UPDATE SET
                source_kind = excluded.source_kind,
                schedule_kind = excluded.schedule_kind,
                title = excluded.title,
                prompt = excluded.prompt,
                cron_expr = excluded.cron_expr,
                timezone = excluded.timezone,
                run_at_ms = excluded.run_at_ms,
                next_fire_at_ms = excluded.next_fire_at_ms,
                last_fired_at_ms = excluded.last_fired_at_ms,
                max_occurrences = excluded.max_occurrences,
                fired_count = excluded.fired_count,
                state = excluded.state,
                created_at_ms = excluded.created_at_ms,
                updated_at_ms = excluded.updated_at_ms,
                node_key = excluded.node_key;
            """, toPersist, tx, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<TrackScheduleDto?> GetAsync(string trackId, string scheduleId, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<TrackScheduleRow>(new CommandDefinition(
            ScheduleSelect + " WHERE track_id=@TrackId AND id=@Id;",
            new { TrackId = trackId, Id = scheduleId }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToDto();
    }

    public async Task<IReadOnlyList<TrackScheduleDto>> ListForTrackAsync(string trackId, bool includeTerminal, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var filter = includeTerminal ? string.Empty : $" AND state='{TrackScheduleStates.Active}'";
        var rows = await conn.QueryAsync<TrackScheduleRow>(new CommandDefinition(
            ScheduleSelect + $" WHERE track_id=@TrackId{filter} ORDER BY CASE WHEN next_fire_at_ms IS NULL THEN 1 ELSE 0 END, next_fire_at_ms ASC, created_at_ms ASC;",
            new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToDto()).ToArray();
    }

    public async Task<IReadOnlyList<FiredTrackSchedule>> ListDueFlowClocksAsync(long nowMs, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<FiredTrackSchedule>(new CommandDefinition("""
            SELECT s.track_id AS TrackId, s.id AS ScheduleId, s.node_key AS NodeKey, s.next_fire_at_ms AS DueAtMs
            FROM track_schedules s
            JOIN tracks t ON t.id = s.track_id
            WHERE s.node_key IS NOT NULL AND s.state = 'active' AND s.next_fire_at_ms <= @Now AND t.closed_at_ms IS NULL
            ORDER BY s.next_fire_at_ms, s.id, s.track_id
            LIMIT 64;
            """, new { Now = nowMs }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToArray();
    }

    public Task<bool> CommitFlowFireAsync(TrackScheduleDto nextClock, long expectedDueAtMs, TaskDto? armedTask,
        EventEnvelopeDto taskEvent, CancellationToken ct)
    {
        _context.EnsureInitialized();
        return _context.Writer.EnqueueAsync(async (conn, tx, token) =>
        {
            var updated = await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE track_schedules
                SET fired_count=@FiredCount, last_fired_at_ms=@LastFiredAtMs, next_fire_at_ms=@NextFireAtMs,
                    state=@State, updated_at_ms=@UpdatedAtMs
                WHERE track_id=@TrackId AND id=@Id AND node_key=@NodeKey
                    AND state='active' AND next_fire_at_ms=@ExpectedDueAtMs
                    AND EXISTS (SELECT 1 FROM tracks WHERE id=@TrackId AND closed_at_ms IS NULL);
                """, new
                {
                    nextClock.TrackId, nextClock.Id, nextClock.NodeKey, nextClock.FiredCount, nextClock.LastFiredAtMs,
                    nextClock.NextFireAtMs, nextClock.State, nextClock.UpdatedAtMs, ExpectedDueAtMs = expectedDueAtMs
                }, tx, cancellationToken: token)).ConfigureAwait(false);
            if (updated == 0)
                return false;
            if (armedTask is not null)
            {
                await _taskRepository.UpsertTaskInternalAsync(conn, tx, armedTask, token).ConfigureAwait(false);
                await EventRepository.InsertEventInternalAsync(conn, tx, taskEvent, token).ConfigureAwait(false);
            }
            await InsertSchedulesEventAsync(conn, tx, nextClock.TrackId, nextClock.UpdatedAtMs, token).ConfigureAwait(false);
            return true;
        }, ct);
    }

    internal static async Task InsertSchedulesEventAsync(SqliteConnection conn, SqliteTransaction tx, string trackId, long now, CancellationToken ct)
    {
        var area = await conn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT area_id FROM tracks WHERE id=@TrackId;",
            new { TrackId = trackId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await EventRepository.InsertEventInternalAsync(conn, tx, new EventEnvelopeDto(null, area, trackId, KernelEventKinds.TrackSchedulesUpdated,
            JsonSerializer.SerializeToElement(new { trackId }), now), ct).ConfigureAwait(false);
    }

    private sealed class TrackScheduleRow
    {
        public string Id { get; set; } = "";
        public string TrackId { get; set; } = "";
        public string SourceKind { get; set; } = "";
        public string ScheduleKind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string? CronExpr { get; set; }
        public string Timezone { get; set; } = "";
        public long? RunAtMs { get; set; }
        public long? NextFireAtMs { get; set; }
        public long? LastFiredAtMs { get; set; }
        public long? MaxOccurrences { get; set; }
        public long FiredCount { get; set; }
        public string State { get; set; } = "";
        public long CreatedAtMs { get; set; }
        public long UpdatedAtMs { get; set; }
        public string? NodeKey { get; set; }

        public TrackScheduleDto ToDto() => new(
            Id, TrackId, SourceKind, ScheduleKind, Title, Prompt,
            CronExpr, Timezone, RunAtMs, NextFireAtMs, LastFiredAtMs,
            MaxOccurrences.HasValue ? checked((int)MaxOccurrences.Value) : null,
            checked((int)FiredCount), State, CreatedAtMs, UpdatedAtMs, NodeKey);
    }
}
