using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore
{
    private static async Task SyncTemplateSchedulesAsync(SqliteConnection conn, SqliteTransaction tx,
        string trackId, IReadOnlyList<TrackScheduleDto> schedules, long now, CancellationToken ct, bool refreshPromptsOnly = false)
    {
        var existing = (await conn.QueryAsync<TrackScheduleRow>(new CommandDefinition(
            ScheduleSelect + " WHERE track_id=@TrackId;", new { TrackId = trackId }, tx, cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(s => s.Id);
        foreach (var schedule in schedules)
        {
            if (schedule.TrackId != trackId || schedule.SourceKind != TrackScheduleSources.Template)
                throw KernelException.InvalidArgument("Invalid template schedule projection.");
            if (existing.TryGetValue(schedule.Id, out var flowClock) && flowClock.NodeKey is not null)
                throw KernelException.Conflict($"Schedule ID '{schedule.Id}' belongs to Track Flow.");
            if (refreshPromptsOnly)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE track_schedules SET prompt=@Prompt, updated_at_ms=@Now
                    WHERE track_id=@TrackId AND id=@Id AND source_kind='template' AND state='active';
                    """, new { schedule.Prompt, TrackId = trackId, schedule.Id, Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
                continue;
            }

            if (existing.TryGetValue(schedule.Id, out var old) && old.SourceKind != TrackScheduleSources.Template)
            {
                if (old.State == TrackScheduleStates.Active)
                    throw KernelException.Conflict($"Schedule ID '{schedule.Id}' belongs to an active non-template schedule.");
                // A terminal non-template row can be reclaimed; active non-template rows are never changed.
            }
            var keepProgress = old is not null && old.SourceKind == TrackScheduleSources.Template && old.State == TrackScheduleStates.Active
                && old.CronExpr == schedule.CronExpr && old.Timezone == schedule.Timezone;
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO track_schedules(id, track_id, source_kind, schedule_kind, title, prompt, cron_expr,
                    timezone, next_fire_at_ms, last_fired_at_ms, fired_count, state, created_at_ms, updated_at_ms)
                VALUES(@Id, @TrackId, 'template', 'cron', @Title, @Prompt, @CronExpr,
                    @Timezone, @NextFireAtMs, @LastFiredAtMs, @FiredCount, 'active', @CreatedAtMs, @Now)
                ON CONFLICT(track_id,id) DO UPDATE SET source_kind='template', schedule_kind='cron', title=excluded.title,
                    prompt=excluded.prompt, cron_expr=excluded.cron_expr, timezone=excluded.timezone, run_at_ms=NULL,
                    next_fire_at_ms=excluded.next_fire_at_ms, last_fired_at_ms=excluded.last_fired_at_ms,
                    fired_count=excluded.fired_count, max_occurrences=NULL, state='active', updated_at_ms=excluded.updated_at_ms;
                """, new { schedule.Id, TrackId = trackId, schedule.Title, schedule.Prompt, schedule.CronExpr, schedule.Timezone,
                    NextFireAtMs = keepProgress ? old!.NextFireAtMs : schedule.NextFireAtMs,
                    LastFiredAtMs = keepProgress ? old!.LastFiredAtMs : null,
                    FiredCount = keepProgress ? old!.FiredCount : 0,
                    CreatedAtMs = old?.CreatedAtMs ?? now, Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        var ids = schedules.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var old in existing.Values.Where(s => !refreshPromptsOnly && s.NodeKey is null && s.SourceKind == TrackScheduleSources.Template && !ids.Contains(s.Id)))
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE track_schedules SET state='canceled', next_fire_at_ms=NULL, updated_at_ms=@Now
                WHERE track_id=@TrackId AND id=@Id;
                UPDATE track_wakes SET state='dropped', reason='template_changed', updated_at_ms=@Now
                WHERE track_id=@TrackId AND kind='schedule' AND coalesce_key=@Key AND state='pending';
                """, new { TrackId = trackId, old.Id, Key = TrackWakeRequests.TrackScheduleKey(old.Id), Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }
        if (schedules.Count > 0 || existing.Values.Any(s => s.SourceKind == TrackScheduleSources.Template))
            await InsertSchedulesEventAsync(conn, tx, trackId, now, ct).ConfigureAwait(false);
    }

    private static async Task InsertSchedulesEventAsync(SqliteConnection conn, SqliteTransaction tx, string trackId, long now, CancellationToken ct)
    {
        var area = await conn.ExecuteScalarAsync<string>(new CommandDefinition("SELECT area_id FROM tracks WHERE id=@TrackId;",
            new { TrackId = trackId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await InsertEventInternalAsync(conn, tx, new EventEnvelopeDto(null, area, trackId, KernelEventKinds.TrackSchedulesUpdated,
            JsonSerializer.SerializeToElement(new { trackId }), now), ct).ConfigureAwait(false);
    }

    public Task<bool> TriggerAsync(string trackId, string scheduleId, CancellationToken ct) => _writer.EnqueueAsync(async (conn, tx, token) =>
    {
        var row = await conn.QuerySingleOrDefaultAsync<TrackScheduleRow>(new CommandDefinition(ScheduleSelect +
            " WHERE track_id=@TrackId AND id=@Id;", new { TrackId = trackId, Id = scheduleId }, tx, cancellationToken: token)).ConfigureAwait(false);
        if (row is null || row.NodeKey is not null) return false;
        if (row.State != TrackScheduleStates.Active) throw KernelException.Conflict("Only active schedules can be triggered.");
        var open = await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM tracks WHERE id=@TrackId AND closed_at_ms IS NULL);",
            new { TrackId = trackId }, tx, cancellationToken: token)).ConfigureAwait(false);
        if (!open) throw KernelException.Conflict("Cannot trigger a closed track.");
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await EnqueueWakeInternalAsync(conn, tx, TrackWakeRequests.DynamicSchedule(trackId, scheduleId, row.Prompt, row.SourceKind), now, token).ConfigureAwait(false);
        await InsertSchedulesEventAsync(conn, tx, trackId, now, token).ConfigureAwait(false);
        return true;
    }, ct);
}
