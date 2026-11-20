using Dapper;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore : ITrackScheduleStore
{
    public ITrackScheduleStore? TrackSchedules => this;

    private const string ScheduleSelect = """
        SELECT id AS Id, track_id AS TrackId,
               source_kind AS SourceKind, schedule_kind AS ScheduleKind, title AS Title,
               prompt AS Prompt, cron_expr AS CronExpr, timezone AS Timezone,
               run_at_ms AS RunAtMs, next_fire_at_ms AS NextFireAtMs, last_fired_at_ms AS LastFiredAtMs,
               max_occurrences AS MaxOccurrences, fired_count AS FiredCount,
               state AS State, created_at_ms AS CreatedAtMs, updated_at_ms AS UpdatedAtMs
        FROM track_schedules
        """;

    public Task<TrackScheduleDto> UpsertAsync(TrackScheduleDto schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        EnsureInitialized();
        return _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            var trackRow = await conn.QuerySingleOrDefaultAsync<TrackClosedRow>(new CommandDefinition(
                "SELECT closed_at_ms AS ClosedAtMs FROM tracks WHERE id=@TrackId;",
                new { schedule.TrackId }, tx, cancellationToken: token)).ConfigureAwait(false);
            if (trackRow is null)
                throw KernelException.NotFound($"Track '{schedule.TrackId}' was not found.");
            if (trackRow.ClosedAtMs is not null)
                throw KernelException.Conflict($"Cannot schedule wake on closed track '{schedule.TrackId}'.");

            var existing = await conn.QuerySingleOrDefaultAsync<TrackScheduleRow>(new CommandDefinition(
                ScheduleSelect + " WHERE track_id=@TrackId AND id=@Id;",
                new { schedule.TrackId, schedule.Id }, tx, cancellationToken: token)).ConfigureAwait(false);

            var toPersist = schedule;
            if (existing is not null &&
                existing.State is TrackScheduleStates.Active)
            {
                var firedCount = checked((int)existing.FiredCount);
                if (schedule.MaxOccurrences is { } max && firedCount >= max)
                    throw KernelException.InvalidArgument($"maxOccurrences ({max}) must exceed current firedCount ({firedCount}).");

                toPersist = schedule with
                {
                    FiredCount = firedCount,
                    LastFiredAtMs = existing.LastFiredAtMs,
                    CreatedAtMs = existing.CreatedAtMs
                };
            }

            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO track_schedules(
                    id, track_id, source_kind, schedule_kind, title, prompt,
                    cron_expr, timezone, run_at_ms, next_fire_at_ms, last_fired_at_ms,
                    max_occurrences, fired_count, state, created_at_ms, updated_at_ms)
                VALUES(
                    @Id, @TrackId, @SourceKind, @ScheduleKind, @Title, @Prompt,
                    @CronExpr, @Timezone, @RunAtMs, @NextFireAtMs, @LastFiredAtMs,
                    @MaxOccurrences, @FiredCount, @State, @CreatedAtMs, @UpdatedAtMs)
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
                    updated_at_ms = excluded.updated_at_ms;
                """, toPersist, tx, cancellationToken: token)).ConfigureAwait(false);

            var row = await conn.QuerySingleAsync<TrackScheduleRow>(new CommandDefinition(
                ScheduleSelect + " WHERE track_id=@TrackId AND id=@Id;",
                new { schedule.TrackId, schedule.Id }, tx, cancellationToken: token)).ConfigureAwait(false);
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

            var coalesceKey = TrackWakeRequests.TrackScheduleKey(existing.Id);
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_wakes SET state='{TrackWakeStates.Dropped}', reason='{TrackWakeReasons.Canceled}', updated_at_ms=@Now
                WHERE track_id=@TrackId AND kind='{TrackWakeKinds.Schedule}' AND coalesce_key=@CoalesceKey AND json_extract(payload_json,'$.scheduleId')=@Id AND state='{TrackWakeStates.Pending}';
                """, new { TrackId = trackId, Id = scheduleId, CoalesceKey = coalesceKey, Now = now }, tx, cancellationToken: token)).ConfigureAwait(false);
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
                WHERE track_id=@TrackId AND kind='{TrackWakeKinds.Schedule}' AND instr(coalesce_key, '{TrackWakeRequests.TrackScheduleKeyPrefix}') = 1 AND state='{TrackWakeStates.Pending}';
                """, new { TrackId = trackId, Now = now }, tx, cancellationToken: token)).ConfigureAwait(false);
            return updated;
        }, ct);
    }

    public async Task<IReadOnlyList<FiredTrackSchedule>> FireDueAsync(
        long nowMs,
        Func<TrackScheduleDto, long?> computeNextFireAtMs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(computeNextFireAtMs);
        EnsureInitialized();
        await using (var read = await OpenReadConnectionAsync(ct).ConfigureAwait(false))
        {
            var anyDue = await read.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(SELECT 1 FROM track_schedules
                WHERE state='active' AND next_fire_at_ms IS NOT NULL AND next_fire_at_ms <= @Now);
                """, new { Now = nowMs }, cancellationToken: ct)).ConfigureAwait(false);
            if (!anyDue) return [];
        }
        return await _writer.EnqueueAsync<IReadOnlyList<FiredTrackSchedule>>(async (conn, tx, token) =>
        {
            var dueRows = (await conn.QueryAsync<TrackScheduleRow>(new CommandDefinition($"""
                SELECT s.id AS Id, s.track_id AS TrackId,
                       s.source_kind AS SourceKind, s.schedule_kind AS ScheduleKind, s.title AS Title,
                       s.prompt AS Prompt, s.cron_expr AS CronExpr, s.timezone AS Timezone,
                       s.run_at_ms AS RunAtMs, s.next_fire_at_ms AS NextFireAtMs, s.last_fired_at_ms AS LastFiredAtMs,
                       s.max_occurrences AS MaxOccurrences, s.fired_count AS FiredCount,
                       s.state AS State, s.created_at_ms AS CreatedAtMs, s.updated_at_ms AS UpdatedAtMs,
                       t.closed_at_ms AS ClosedAtMs
                FROM track_schedules s
                JOIN tracks t ON t.id = s.track_id
                WHERE s.state='{TrackScheduleStates.Active}' AND s.next_fire_at_ms IS NOT NULL AND s.next_fire_at_ms <= @Now
                ORDER BY s.next_fire_at_ms ASC, s.id ASC
                LIMIT 64;
                """, new { Now = nowMs }, tx, cancellationToken: token)).ConfigureAwait(false)).AsList();

            if (dueRows.Count == 0)
                return [];

            var fired = new List<FiredTrackSchedule>(dueRows.Count);
            foreach (var row in dueRows)
            {
                if (row.ClosedAtMs is not null)
                {
                    await conn.ExecuteAsync(new CommandDefinition("""
                        UPDATE track_schedules SET state='canceled', next_fire_at_ms=NULL, updated_at_ms=@Now
                        WHERE track_id=@TrackId AND id=@Id AND state='active';
                        """, new { row.TrackId, row.Id, Now = nowMs }, tx, cancellationToken: token)).ConfigureAwait(false);
                    continue;
                }
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
                    WHERE track_id=@TrackId AND id=@Id AND state='active';
                    """, new
                {
                    schedule.TrackId,
                    schedule.Id,
                    FiredCount = nextFiredCount,
                    Now = nowMs,
                    NextFireAtMs = nextFireAtMs,
                    State = nextState
                }, tx, cancellationToken: token)).ConfigureAwait(false);

                var wakeRequest = TrackWakeRequests.DynamicSchedule(
                    schedule.TrackId,
                    schedule.Id,
                    schedule.Prompt);
                await EnqueueWakeInternalAsync(conn, tx, wakeRequest, nowMs, token).ConfigureAwait(false);
                fired.Add(new FiredTrackSchedule(schedule.TrackId, schedule.Id));
            }

            return fired;
        }, ct);
    }

    private sealed class TrackClosedRow
    {
        public long? ClosedAtMs { get; set; }
    }

    private sealed class TrackScheduleRow
    {
        public long? ClosedAtMs { get; set; }
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

        public TrackScheduleDto ToDto() => new(
            Id, TrackId, SourceKind, ScheduleKind, Title, Prompt,
            CronExpr, Timezone, RunAtMs, NextFireAtMs, LastFiredAtMs,
            MaxOccurrences.HasValue ? checked((int)MaxOccurrences.Value) : null,
            checked((int)FiredCount), State, CreatedAtMs, UpdatedAtMs);
    }
}
