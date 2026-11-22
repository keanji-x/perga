using Dapper;
using Perga.Contracts;

namespace Perga.Storage.Sqlite;

/// <summary>Monotonic read receipts and manual unread markers.</summary>
internal sealed class ReadReceiptsRepository
{
    private readonly SqliteStoreContext _context;

    internal ReadReceiptsRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public async Task<ReadReceiptsResponseDto> GetReadReceiptsAsync(CancellationToken ct = default)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var baseline = await conn.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT baseline_ms FROM read_receipt_meta WHERE id = 1;", cancellationToken: ct)).ConfigureAwait(false);
        var receipts = await conn.QueryAsync<ReadReceiptRow>(new CommandDefinition(
            "SELECT kind AS Kind, target_id AS TargetId, read_through_ms AS ReadThroughMs, manual_unread AS ManualUnread FROM read_receipts ORDER BY kind, target_id;",
            cancellationToken: ct)).ConfigureAwait(false);
        return new(baseline, receipts.Select(r => r.ToDto()).ToArray());
    }

    private sealed class ReadReceiptRow
    {
        public string Kind { get; init; } = "";
        public string TargetId { get; init; } = "";
        public long ReadThroughMs { get; init; }
        public long ManualUnread { get; init; }
        public ReadReceiptDto ToDto() => new(Kind, TargetId, ReadThroughMs, ManualUnread != 0);
    }

    public Task<ReadReceiptDto> MarkReadAsync(string kind, string targetId, long readThroughMs, CancellationToken ct = default) =>
        WriteReadReceiptAsync(kind, targetId, readThroughMs, false, ct);

    public Task<ReadReceiptDto> MarkUnreadAsync(string kind, string targetId, CancellationToken ct = default) =>
        WriteReadReceiptAsync(kind, targetId, 0, true, ct);

    private Task<ReadReceiptDto> WriteReadReceiptAsync(string kind, string targetId, long readThroughMs, bool manualUnread, CancellationToken ct)
    {
        _context.EnsureInitialized();
        if (kind is not ("track" or "conversation") || string.IsNullOrWhiteSpace(targetId) || readThroughMs < 0)
            throw new ArgumentException("Invalid read receipt.");
        return _context.Writer.EnqueueAsync<ReadReceiptDto>(async (conn, tx, innerCt) =>
            (await conn.QuerySingleAsync<ReadReceiptRow>(new CommandDefinition("""
                INSERT INTO read_receipts(kind, target_id, read_through_ms, manual_unread, updated_at_ms)
                VALUES (@Kind, @TargetId, @ReadThroughMs, @ManualUnread, @Now)
                ON CONFLICT(kind, target_id) DO UPDATE SET
                    read_through_ms = MAX(read_receipts.read_through_ms, excluded.read_through_ms),
                    manual_unread = excluded.manual_unread, updated_at_ms = excluded.updated_at_ms
                RETURNING kind AS Kind, target_id AS TargetId, read_through_ms AS ReadThroughMs, manual_unread AS ManualUnread;
                """, new { Kind = kind, TargetId = targetId, ReadThroughMs = readThroughMs, ManualUnread = manualUnread,
                    Now = _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() }, tx, cancellationToken: innerCt)).ConfigureAwait(false)).ToDto(), ct);
    }
}
