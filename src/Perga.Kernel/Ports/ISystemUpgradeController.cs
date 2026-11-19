using System.Text.Json;
using Perga.Contracts;

namespace Perga.Kernel.Ports;

public interface ISystemUpgradeController
{
    Task<JsonElement> CheckAsync(string? sourceId, CancellationToken ct);
    Task<SystemUpgradeHandoff> ScheduleAsync(bool rollback, string? sourceId, string notifyTrackId, CancellationToken ct);
}
public interface ISystemUpgradeReceiptSource
{
    Task<SystemUpgradeReceipt?> ReadAsync(CancellationToken ct);
}
public interface ISystemUpgradeStore
{
    Task<int> GetSchemaVersionAsync(CancellationToken ct);
    // Atomically consume the job and enqueue its durable system wake.
    Task<bool> ConsumeUpgradeReceiptAsync(SystemUpgradeReceipt receipt, CancellationToken ct);
    Task<IReadOnlyList<string>> PrepareUpgradeNotificationRetriesAsync(CancellationToken ct);
    Task AcknowledgeUpgradeNotificationAsync(string mailId, CancellationToken ct);
    Task MarkUpgradeNotificationFailedAsync(string mailId, string error, CancellationToken ct);
}
