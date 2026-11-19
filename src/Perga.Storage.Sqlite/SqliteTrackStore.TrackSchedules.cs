using Dapper;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore : ITrackScheduleStore
{
    public ITrackScheduleStore? TrackSchedules => this;

    private const string ScheduleSelect = """
        SELECT id AS Id, track_id AS TrackId, target_session_id AS TargetSessionId,
               source_kind AS SourceKind, schedule_kind AS ScheduleKind, title AS Title,
               prompt AS Prompt, cron_expr AS CronExpr, timezone AS Timezone,
               run_at_ms AS RunAtMs, next_fire_at_ms AS NextFireAtMs, last_fired_at_ms AS LastFiredAtMs,
               max_occurrences AS MaxOccurrences, fired_count AS FiredCount, coalesce_key AS CoalesceKey,
               state AS State, created_at_ms AS CreatedAtMs, updated_at_ms AS UpdatedAtMs
        FROM track_schedules
        """;

    public Task<TrackScheduleDto> UpsertAsync(TrackScheduleDto schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        EnsureInitialized();
        return _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            var closedAtMs = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT closed_at_ms FROM tracks WHERE id=@TrackId;",
                new { schedule.TrackId }, tx, cancellationToken: token)).ConfigureAwait(false);
            var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM tracks WHERE id=@TrackId);",
                new { schedule.TrackId }, tx, cancellationToken: token)).ConfigureAwait(false);
            if (!exists)
                throw KernelException.NotFound($"Track '{schedule.TrackId}' was not found.");
            if (closedAtMs is not null)
                throw KernelException.Conflict($"Cannot schedule wake on closed track '{schedule.TrackId}'.");

            var existingTrackId = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT track_id FROM track_schedules WHERE id=@Id;",
                new { schedule.Id }, tx, cancellationToken: token)).ConfigureAwait(false);
            if (existingTrackId is not null && !string.Equals(existingTrackId, schedule.TrackId, StringComparison.Ordinal))
                throw KernelException.Conflict($"Schedule '{schedule.Id}' belongs to another track.");

            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO track_schedules(
                    id, track_id, target_session_id, source_kind, schedule_kind, title, prompt,
                    cron_expr, timezone, run_at_ms, next_fire_at_ms, last_fired_at_ms,
                    max_occurrences, fired_count, coalesce_key, state, created_at_ms, updated_at_ms)
                VALUES(
                    @Id, @TrackId, @TargetSessionId, @SourceKind, @ScheduleKind, @Title, @Prompt,
                    @CronExpr, @Timezone, @RunAtMs, @NextFireAtMs, @LastFiredAtMs,
                    @MaxOccurrences, @FiredCount, @CoalesceKey, @State, @CreatedAtMs, @UpdatedAtMs)
                ON CONFLICT(id) DO UPDATE SET
                    target_session_id = excluded.target_session_id,
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
                    coalesce_key = excluded.coalesce_key,
                    state = excluded.state,
                    updated_at_ms = excluded.updated_at_ms;
                """, schedule, tx, cancellationToken: token)).ConfigureAwait(false);

            var row = await conn.QuerySingleAsync<TrackScheduleRow>(new CommandDefinition(
                ScheduleSelect + " WHERE id=@Id;", new { schedule.Id }, tx, cancellationToken: token)).ConfigureAwait(false);
            return row.ToDto();
        }, ct);
    }

    public async Task<TrackScheduleDto?> GetAsync(string trackId, string scheduleId, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<TrackScheduleRow>(new CommandDefinition(
            ScheduleSelect + " WHERE track_id=@TrackId AND id=@Id;",
            new { TrackId = trackId, Id = scheduleId }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToDto();
    }

    public async Task<IReadOnlyList<TrackScheduleDto>> ListForTrackAsync(string trackId, bool includeTerminal, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var filter = includeTerminal ? string.Empty : $" AND state='{TrackScheduleStates.Active}'";
        var rows = await conn.QueryAsync<TrackScheduleRow>(new CommandDefinition(
            ScheduleSelect + $" WHERE track_id=@TrackId{filter} ORDER BY CASE WHEN next_fire_at_ms IS NULL THEN 1 ELSE 0 END, next_fire_at_ms ASC, created_at_ms ASC;",
            new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => r.ToDto()).ToArray();
    }

    public Task<bool> CancelAsync(string trackId, string scheduleId, CancellationToken ct)
    {
        EnsureInitialized();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            var existing = await conn.QuerySingleOrDefaultAsync<TrackScheduleRow>(new CommandDefinition(
                ScheduleSelect + " WHERE track_id=@TrackId AND id=@Id;",
                new { TrackId = trackId, Id = scheduleId }, tx, cancellationToken: token)).ConfigureAwait(false);
            if (existing is null || existing.State != TrackScheduleStates.Active)
                return false;

            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_schedules SET state='{TrackScheduleStates.Canceled}', next_fire_at_ms=NULL, updated_at_ms=@Now
                WHERE track_id=@TrackId AND id=@Id AND state='{TrackScheduleStates.Active}';
                """, new { TrackId = trackId, Id = scheduleId, Now = now }, tx, cancellationToken: token)).ConfigureAwait(false);

            var coalesceKey = existing.CoalesceKey ?? TrackWakeRequests.ScheduleKey(existing.Id);
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_wakes SET state='{TrackWakeStates.Dropped}', reason='{TrackWakeReasons.Canceled}', updated_at_ms=@Now
                WHERE track_id=@TrackId AND kind='{TrackWakeKinds.Schedule}' AND coalesce_key=@CoalesceKey AND state='{TrackWakeStates.Pending}';
                """, new { TrackId = trackId, CoalesceKey = coalesceKey, Now = now }, tx, cancellationToken: token)).ConfigureAwait(false);
            return true;
        }, ct);
    }

    public Task<int> CancelActiveForTrackAsync(string trackId, CancellationToken ct)
    {
        EnsureInitialized();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        return _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            var updated = await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_schedules SET state='{TrackScheduleStates.Canceled}', next_fire_at_ms=NULL, updated_at_ms=@Now
                WHERE track_id=@TrackId AND state='{TrackScheduleStates.Active}';
                """, new { TrackId = trackId, Now = now }, tx, cancellationToken: token)).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_wakes SET state='{TrackWakeStates.Dropped}', reason='{TrackWakeReasons.Canceled}', updated_at_ms=@Now
                WHERE track_id=@TrackId AND kind='{TrackWakeKinds.Schedule}' AND state='{TrackWakeStates.Pending}';
                """, new { TrackId = trackId, Now = now }, tx, cancellationToken: token)).ConfigureAwait(false);
            return updated;
        }, ct);
    }

    public Task<IReadOnlyList<FiredTrackSchedule>> FireDueAsync(
        long nowMs,
        Func<TrackScheduleDto, long?> computeNextFireAtMs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(computeNextFireAtMs);
        EnsureInitialized();
        return _writer.EnqueueAsync<IReadOnlyList<FiredTrackSchedule>>(async (conn, tx, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_schedules SET state='{TrackScheduleStates.Canceled}', next_fire_at_ms=NULL, updated_at_ms=@Now
                WHERE state='{TrackScheduleStates.Active}' AND EXISTS (
                    SELECT 1 FROM tracks t WHERE t.id=track_id AND t.closed_at_ms IS NOT NULL);
                """, new { Now = nowMs }, tx, cancellationToken: token)).ConfigureAwait(false);

            var dueRows = (await conn.QueryAsync<TrackScheduleRow>(new CommandDefinition($"""
                SELECT s.id AS Id, s.track_id AS TrackId, s.target_session_id AS TargetSessionId,
                       s.source_kind AS SourceKind, s.schedule_kind AS ScheduleKind, s.title AS Title,
                       s.prompt AS Prompt, s.cron_expr AS CronExpr, s.timezone AS Timezone,
                       s.run_at_ms AS RunAtMs, s.next_fire_at_ms AS NextFireAtMs, s.last_fired_at_ms AS LastFiredAtMs,
                       s.max_occurrences AS MaxOccurrences, s.fired_count AS FiredCount, s.coalesce_key AS CoalesceKey,
                       s.state AS State, s.created_at_ms AS CreatedAtMs, s.updated_at_ms AS UpdatedAtMs
                FROM track_schedules s
                JOIN tracks t ON t.id = s.track_id
                WHERE s.state='{TrackScheduleStates.Active}' AND s.next_fire_at_ms IS NOT NULL AND s.next_fire_at_ms <= @Now
                  AND t.closed_at_ms IS NULL
                ORDER BY s.next_fire_at_ms ASC, s.id ASC
                LIMIT 64;
                """, new { Now = nowMs }, tx, cancellationToken: token)).ConfigureAwait(false)).AsList();

            if (dueRows.Count == 0)
                return [];

            var fired = new List<FiredTrackSchedule>(dueRows.Count);
            foreach (var row in dueRows)
            {
                var schedule = row.ToDto();
                var nextFiredCount = schedule.FiredCount + 1;
                long? nextFireAtMs = null;
                var nextState = TrackScheduleStates.Done;
                if (schedule.ScheduleKind == TrackScheduleKinds.Cron &&
                    (!schedule.MaxOccurrences.HasValue || nextFiredCount < schedule.MaxOccurrences.Value))
                {
                    nextFireAtMs = computeNextFireAtMs(schedule);
                    if (nextFireAtMs.HasValue)
                        nextState = TrackScheduleStates.Active;
                }

                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE track_schedules
                    SET fired_count=@FiredCount, last_fired_at_ms=@Now, next_fire_at_ms=@NextFireAtMs,
                        state=@State, updated_at_ms=@Now
                    WHERE id=@Id AND state='active';
                    """, new
                {
                    schedule.Id,
                    FiredCount = nextFiredCount,
                    Now = nowMs,
                    NextFireAtMs = nextFireAtMs,
                    State = nextState
                }, tx, cancellationToken: token)).ConfigureAwait(false);

                var wakeRequest = TrackWakeRequests.DynamicSchedule(
                    schedule.TrackId,
                    schedule.Id,
                    schedule.Prompt,
                    schedule.CoalesceKey,
                    schedule.TargetSessionId);
                await EnqueueWakeInternalAsync(conn, tx, wakeRequest, nowMs, token).ConfigureAwait(false);
                fired.Add(new FiredTrackSchedule(schedule.TrackId, schedule.Id));
            }

            return fired;
        }, ct);
    }

    private sealed class TrackScheduleRow
    {
        public string Id { get; set; } = "";
        public string TrackId { get; set; } = "";
        public string? TargetSessionId { get; set; }
        public string SourceKind { get; set; } = "";
        public string ScheduleKind { get; set; } = "";
        public string Title { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string? CronExpr { get; set; }
        public string Timezone { get; set; } = "";
        public long? RunAtMs { get; set; }
        public long? NextFireAtMs { get; set; }
        public long? LastFiredAtMs { get; set; }
        public int? MaxOccurrences { get; set; }
        public int FiredCount { get; set; }
        public string? CoalesceKey { get; set; }
        public string State { get; set; } = "";
        public long CreatedAtMs { get; set; }
        public long UpdatedAtMs { get; set; }

        public TrackScheduleDto ToDto() => new(
            Id, TrackId, TargetSessionId, SourceKind, ScheduleKind, Title, Prompt,
            CronExpr, Timezone, RunAtMs, NextFireAtMs, LastFiredAtMs,
            MaxOccurrences, FiredCount, CoalesceKey, State, CreatedAtMs, UpdatedAtMs);
    }
}
