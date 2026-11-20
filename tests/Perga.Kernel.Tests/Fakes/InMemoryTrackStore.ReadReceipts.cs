using Perga.Contracts;

namespace Perga.Kernel.Tests.Fakes;

internal sealed partial class InMemoryTrackStore
{
    private readonly Dictionary<(string, string), ReadReceiptDto> _receipts = new();
    public Task<ReadReceiptsResponseDto> GetReadReceiptsAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(new ReadReceiptsResponseDto(0, _receipts.Values.ToArray()));
    }
    public Task<ReadReceiptDto> MarkReadAsync(string kind, string targetId, long readThroughMs, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var value = new ReadReceiptDto(kind, targetId, Math.Max(readThroughMs, _receipts.GetValueOrDefault((kind, targetId))?.ReadThroughMs ?? 0), false);
            _receipts[(kind, targetId)] = value;
            return Task.FromResult(value);
        }
    }
    public Task<ReadReceiptDto> MarkUnreadAsync(string kind, string targetId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var value = new ReadReceiptDto(kind, targetId, _receipts.GetValueOrDefault((kind, targetId))?.ReadThroughMs ?? 0, true);
            _receipts[(kind, targetId)] = value;
            return Task.FromResult(value);
        }
    }
}
