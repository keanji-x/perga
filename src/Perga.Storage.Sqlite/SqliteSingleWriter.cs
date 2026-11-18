using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Perga.Storage.Sqlite;

/// <summary>
/// Single-writer micro-batching channel over a dedicated <c>Mode=ReadWriteCreate;Pooling=False</c> WAL SQLite connection.
/// Drains up to <c>MaxBatchSize</c> (32) write closures per <c>BEGIN IMMEDIATE ... COMMIT</c> transaction, eliminating
/// <c>SQLITE_BUSY</c> contention across concurrent writers and lock-free snapshot readers.
/// </summary>
public sealed class SqliteSingleWriter : IAsyncDisposable
{
    private readonly SqliteStorageOptions _options;
    private readonly Channel<WriteBatchItem> _channel;
    private readonly CancellationTokenSource _shutdownCts = new();
    private SqliteConnection? _writeConnection;
    private Task? _pumpTask;
    private int _disposed;

    public SqliteSingleWriter(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.WriteChannelCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxBatchSize, 0);

        _options = options;
        _channel = Channel.CreateBounded<WriteBatchItem>(new BoundedChannelOptions(options.WriteChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public bool IsInitialized => _writeConnection is not null;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_writeConnection is not null)
        {
            return;
        }

        var fullDbPath = _options.GetFullDbPath();
        var parentDir = Path.GetDirectoryName(fullDbPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = fullDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = _options.BusyTimeoutSeconds
        };

        var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var busyMs = Math.Max(1, _options.BusyTimeoutSeconds) * 1000;
        await conn.ExecuteAsync(
            new CommandDefinition(
                $"""
                PRAGMA busy_timeout = {busyMs};
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA temp_store = MEMORY;
                """,
                cancellationToken: ct)).ConfigureAwait(false);

        _writeConnection = conn;
        _pumpTask = Task.Run(() => PumpLoopAsync(_shutdownCts.Token), CancellationToken.None);
    }

    public Task EnqueueAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask> operation,
        CancellationToken ct = default) =>
        EnqueueAsync<object?>(
            async (conn, tx, innerCt) =>
            {
                await operation(conn, tx, innerCt).ConfigureAwait(false);
                return null;
            },
            ct);

    public async Task<T> EnqueueAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnsureReady();
        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WriteBatchItem(
            IsTransactional: true,
            TransactionalOp: async (conn, tx, opCt) =>
            {
                var result = await operation(conn, tx, opCt).ConfigureAwait(false);
                return result;
            },
            RawConnectionOp: null,
            CompleteSuccess: val => tcs.TrySetResult((T)val!),
            CompleteError: ex => tcs.TrySetException(ex),
            CompleteCanceled: cancelCt => tcs.TrySetCanceled(cancelCt),
            CallerToken: ct);

        await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an exclusive maintenance command (such as <c>PRAGMA incremental_vacuum</c> or <c>PRAGMA wal_checkpoint</c>)
    /// serialized on the single writer connection outside of an active transaction.
    /// </summary>
    public async Task<T> EnqueueNonTransactionalAsync<T>(
        Func<SqliteConnection, CancellationToken, ValueTask<T>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnsureReady();
        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WriteBatchItem(
            IsTransactional: false,
            TransactionalOp: null,
            RawConnectionOp: async (conn, opCt) =>
            {
                var result = await operation(conn, opCt).ConfigureAwait(false);
                return result;
            },
            CompleteSuccess: val => tcs.TrySetResult((T)val!),
            CompleteError: ex => tcs.TrySetException(ex),
            CompleteCanceled: cancelCt => tcs.TrySetCanceled(cancelCt),
            CallerToken: ct);

        await _channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_writeConnection is null)
        {
            throw new InvalidOperationException("SqliteSingleWriter has not been initialized. Call InitializeAsync first.");
        }
    }

    private async Task PumpLoopAsync(CancellationToken shutdownToken)
    {
        var reader = _channel.Reader;
        var batch = new List<WriteBatchItem>(_options.MaxBatchSize);
        var results = new object?[_options.MaxBatchSize];

        try
        {
            while (await reader.WaitToReadAsync(shutdownToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var first))
                {
                    if (!first.IsTransactional)
                    {
                        await ExecuteNonTransactionalItemAsync(first).ConfigureAwait(false);
                        continue;
                    }

                    batch.Clear();
                    if (first.CallerToken.IsCancellationRequested)
                    {
                        first.CompleteCanceled(first.CallerToken);
                    }
                    else
                    {
                        batch.Add(first);
                    }

                    WriteBatchItem? deferredNonTx = null;
                    while (batch.Count < _options.MaxBatchSize && reader.TryRead(out var next))
                    {
                        if (!next.IsTransactional)
                        {
                            deferredNonTx = next;
                            break;
                        }

                        if (next.CallerToken.IsCancellationRequested)
                        {
                            next.CompleteCanceled(next.CallerToken);
                            continue;
                        }

                        batch.Add(next);
                    }

                    if (batch.Count > 0)
                    {
                        await ExecuteBatchAsync(batch, results).ConfigureAwait(false);
                    }

                    if (deferredNonTx is not null)
                    {
                        await ExecuteNonTransactionalItemAsync(deferredNonTx).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        finally
        {
            while (reader.TryRead(out var leftover))
            {
                leftover.CompleteError(new ObjectDisposedException(nameof(SqliteSingleWriter)));
            }
        }
    }

    private async Task ExecuteNonTransactionalItemAsync(WriteBatchItem item)
    {
        if (item.CallerToken.IsCancellationRequested)
        {
            item.CompleteCanceled(item.CallerToken);
            return;
        }

        try
        {
            var result = await item.RawConnectionOp!(_writeConnection!, item.CallerToken).ConfigureAwait(false);
            item.CompleteSuccess(result);
        }
        catch (OperationCanceledException) when (item.CallerToken.IsCancellationRequested)
        {
            item.CompleteCanceled(item.CallerToken);
        }
        catch (Exception ex)
        {
            item.CompleteError(ex);
        }
    }

    private async Task ExecuteBatchAsync(List<WriteBatchItem> batch, object?[] results)
    {
        var conn = _writeConnection!;

        if (batch.Count == 1)
        {
            await ExecuteSingleItemTransactionAsync(conn, batch[0]).ConfigureAwait(false);
            return;
        }

        Array.Clear(results, 0, batch.Count);
        Exception? batchFailure = null;

        await using (var tx = conn.BeginTransaction(deferred: false))
        {
            try
            {
                for (var i = 0; i < batch.Count; i++)
                {
                    var item = batch[i];
                    // Pass CancellationToken.None inside the shared batch transaction if already entered,
                    // or honor CallerToken and trigger individual replay on cancellation/error so sibling items stay isolated.
                    item.CallerToken.ThrowIfCancellationRequested();
                    results[i] = await item.TransactionalOp!(conn, tx, item.CallerToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                batchFailure = ex;
                try
                {
                    await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort rollback on failed transaction
                }
            }
        }

        if (batchFailure is null)
        {
            for (var i = 0; i < batch.Count; i++)
            {
                batch[i].CompleteSuccess(results[i]);
            }

            return;
        }

        // Batch rolled back cleanly: replay each item in its own BEGIN IMMEDIATE transaction so valid sibling callers
        // succeed while the failing caller receives its exact exception without deadlocking.
        for (var i = 0; i < batch.Count; i++)
        {
            await ExecuteSingleItemTransactionAsync(conn, batch[i]).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteSingleItemTransactionAsync(SqliteConnection conn, WriteBatchItem item)
    {
        if (item.CallerToken.IsCancellationRequested)
        {
            item.CompleteCanceled(item.CallerToken);
            return;
        }

        await using var tx = conn.BeginTransaction(deferred: false);
        try
        {
            var result = await item.TransactionalOp!(conn, tx, item.CallerToken).ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            item.CompleteSuccess(result);
        }
        catch (OperationCanceledException) when (item.CallerToken.IsCancellationRequested)
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore rollback failure
            }

            item.CompleteCanceled(item.CallerToken);
        }
        catch (Exception ex)
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore rollback failure
            }

            item.CompleteError(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch
            {
                // Drained
            }
        }

        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        _shutdownCts.Dispose();

        if (_writeConnection is not null)
        {
            await _writeConnection.CloseAsync().ConfigureAwait(false);
            await _writeConnection.DisposeAsync().ConfigureAwait(false);
            _writeConnection = null;
        }
    }

    private sealed record WriteBatchItem(
        bool IsTransactional,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<object?>>? TransactionalOp,
        Func<SqliteConnection, CancellationToken, ValueTask<object?>>? RawConnectionOp,
        Action<object?> CompleteSuccess,
        Action<Exception> CompleteError,
        Action<CancellationToken> CompleteCanceled,
        CancellationToken CallerToken);
}
