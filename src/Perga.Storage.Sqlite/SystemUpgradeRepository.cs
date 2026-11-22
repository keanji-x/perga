using System.Text.Json;
using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Upgrade receipts and retryable upgrade notification delivery.</summary>
internal sealed class SystemUpgradeRepository
{
    private readonly SqliteStoreContext _context;
    private readonly CorrespondenceRepository _correspondenceRepository;

    internal SystemUpgradeRepository(SqliteStoreContext context, CorrespondenceRepository correspondenceRepository)
    {
        _context = context;
        _correspondenceRepository = correspondenceRepository;
    }

    private const string UpgradeMailIdPrefix = TrackWakeRequests.SystemUpgradeMailIdPrefix;

    private const string UpgradeWakeKeyPrefix = TrackWakeRequests.SystemUpgradeMailKeyPrefix;

    public async Task<int> GetSchemaVersionAsync(CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;", cancellationToken: ct)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<string>> PrepareUpgradeNotificationRetriesAsync(CancellationToken ct) =>
        _context.Writer.EnqueueAsync<IReadOnlyList<string>>(async (conn, tx, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_wakes SET state='{TrackWakeStates.Pending}', turn_id=NULL, reason='{TrackWakeReasons.UpgradeRetry}'
                WHERE state='{TrackWakeStates.Delivering}' AND coalesce_key IN (
                    SELECT '{UpgradeWakeKeyPrefix}' || job_id FROM system_upgrade_receipts
                    WHERE notification_status LIKE 'delivery_failed:%');
                UPDATE system_upgrade_receipts SET notification_status='queued'
                WHERE notification_status LIKE 'delivery_failed:%' AND EXISTS (
                    SELECT 1 FROM track_wakes w WHERE w.coalesce_key='{UpgradeWakeKeyPrefix}' || job_id AND w.state='{TrackWakeStates.Pending}');
                -- Repair projections if delivery committed before receipt acknowledgement.
                UPDATE system_upgrade_receipts SET notification_status='notified'
                WHERE notification_status='queued' AND EXISTS (
                    SELECT 1 FROM track_wakes w WHERE w.coalesce_key='{UpgradeWakeKeyPrefix}' || job_id AND w.state='{TrackWakeStates.Done}');
                -- Only undelivered notifications become unavailable; 'notified' is final even if the track closes later.
                -- Deleting a track cascades away its mail, so absence of an open-track mail covers both closure and deletion.
                UPDATE system_upgrade_receipts SET notification_status='target_unavailable'
                WHERE (notification_status='queued' OR notification_status LIKE 'delivery_failed:%') AND NOT EXISTS (
                    SELECT 1 FROM mails m JOIN tracks t ON t.id=m.to_track_id
                    WHERE m.id='{UpgradeMailIdPrefix}' || job_id AND t.closed_at_ms IS NULL);
                UPDATE mails SET wake_pending=0 WHERE id IN (
                    SELECT '{UpgradeMailIdPrefix}' || job_id FROM system_upgrade_receipts
                    WHERE notification_status IN ('notified','target_unavailable'));
                """, transaction: tx, cancellationToken: token)).ConfigureAwait(false);
            return (await conn.QueryAsync<string>(new CommandDefinition($"""
                SELECT DISTINCT w.track_id FROM track_wakes w
                JOIN system_upgrade_receipts r ON w.coalesce_key='{UpgradeWakeKeyPrefix}' || r.job_id
                JOIN tracks t ON t.id=w.track_id AND t.closed_at_ms IS NULL
                WHERE w.state='{TrackWakeStates.Pending}';
                """, transaction: tx, cancellationToken: token)).ConfigureAwait(false)).AsList();
        }, ct);

    public Task AcknowledgeUpgradeNotificationAsync(string mailId, CancellationToken ct) => _correspondenceRepository.AcknowledgeMailWakeAsync(mailId, ct);

    public Task MarkUpgradeNotificationFailedAsync(string mailId, string error, CancellationToken ct) =>
        _context.Writer.EnqueueAsync(async (conn, tx, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE system_upgrade_receipts SET notification_status = @Status WHERE '{UpgradeMailIdPrefix}' || job_id = @MailId;",
                new { Status = "delivery_failed: " + error, MailId = mailId }, tx, cancellationToken: token)).ConfigureAwait(false);
        }, ct);

    public Task<bool> ConsumeUpgradeReceiptAsync(SystemUpgradeReceipt receipt, CancellationToken ct)
    {
        _context.EnsureInitialized();
        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return _context.Writer.EnqueueAsync<bool>(async (conn, tx, token) =>
        {
            var now = _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var target = receipt.NotifyTrackId is null ? null : await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT id FROM tracks WHERE id = @Id AND closed_at_ms IS NULL AND lead_session_id IS NOT NULL;",
                new { Id = receipt.NotifyTrackId }, tx, cancellationToken: token)).ConfigureAwait(false);
            var inserted = await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO system_upgrade_receipts(job_id, receipt_json, consumed_at_ms, notification_status)
                VALUES (@JobId, @Json, @Now, @Status) ON CONFLICT(job_id) DO NOTHING;
                """, new { receipt.JobId, Json = json, Now = now,
                    Status = receipt.NotifyTrackId is null ? "not_requested" : target is null ? "target_unavailable" : "queued" }, tx,
                cancellationToken: token)).ConfigureAwait(false);
            if (inserted == 0) return false;
            var evt = new EventEnvelopeDto(null, null, target, "system.upgrade_result",
                JsonSerializer.SerializeToElement(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)), now);
            await EventRepository.InsertEventInternalAsync(conn, tx, evt, token).ConfigureAwait(false);
            if (target is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO mails(id, from_track_id, to_track_id, summary, body, wake_pending, created_at_ms)
                    VALUES (@Id, 'system:upgrade', @Target, @Summary, @Json, 1, @Now);
                    """, new { Id = TrackWakeRequests.SystemUpgradeMailId(receipt.JobId), Target = target,
                        Summary = "System upgrade " + receipt.Status, Json = json, Now = now }, tx,
                    cancellationToken: token)).ConfigureAwait(false);
                await TrackWakesRepository.EnqueueWakeInternalAsync(conn, tx, TrackWakeRequests.Mail(new MailDto(
                    TrackWakeRequests.SystemUpgradeMailId(receipt.JobId), "system:upgrade", target,
                    "System upgrade " + receipt.Status, json, null, now)), now, token).ConfigureAwait(false);
            }
            return true;
        }, ct);
    }
}
