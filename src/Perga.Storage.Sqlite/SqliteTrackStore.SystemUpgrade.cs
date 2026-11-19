using System.Text.Json;
using Dapper;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore : ISystemUpgradeStore
{
    public async Task<int> GetSchemaVersionAsync(CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;", cancellationToken: ct)).ConfigureAwait(false);
    }

    public Task MarkUpgradeNotificationFailedAsync(string mailId, string error, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE system_upgrade_receipts SET notification_status = @Status WHERE 'system-upgrade:' || job_id = @MailId;",
                new { Status = "delivery_failed: " + error, MailId = mailId }, tx, cancellationToken: token)).ConfigureAwait(false);
        }, ct);

    public Task<bool> ConsumeUpgradeReceiptAsync(SystemUpgradeReceipt receipt, CancellationToken ct)
    {
        EnsureInitialized();
        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return _writer.EnqueueAsync<bool>(async (conn, tx, token) =>
        {
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
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
            await InsertEventInternalAsync(conn, tx, evt, token).ConfigureAwait(false);
            if (target is not null)
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO mails(id, from_track_id, to_track_id, summary, body, wake_pending, created_at_ms)
                    VALUES (@Id, 'system:upgrade', @Target, @Summary, @Json, 1, @Now);
                    """, new { Id = "system-upgrade:" + receipt.JobId, Target = target,
                        Summary = "System upgrade " + receipt.Status, Json = json, Now = now }, tx,
                    cancellationToken: token)).ConfigureAwait(false);
            return true;
        }, ct);
    }
}
