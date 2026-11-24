using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Owns the single writer, reader factory and shared storage lifetime.</summary>
internal sealed class SqliteStoreContext : IAsyncDisposable
{
    private readonly SqliteStorageOptions _options;

    private readonly SqliteMigrator _migrator;

    private readonly SqliteSingleWriter _writer;

    private readonly ColdBlobStore _coldBlobStore;

    private readonly SqliteJsonUpcaster _upcaster;

    private readonly TimeProvider _timeProvider;

    private readonly string _readConnectionString;

    private readonly SemaphoreSlim _initLock = new(1, 1);

    private string _databaseId = string.Empty;

    private bool _initialized;

    private int _disposed;

    internal SqliteStoreContext(
        SqliteStorageOptions options,
        SqliteMigrator? migrator = null,
        TimeProvider? timeProvider = null,
        SqliteJsonUpcaster? upcaster = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DbPath);

        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _upcaster = upcaster ?? SqliteJsonUpcaster.Default;
        _migrator = migrator ?? new SqliteMigrator(timeProvider: _timeProvider);
        _writer = new SqliteSingleWriter(options);
        _coldBlobStore = new ColdBlobStore(options, _upcaster);
        Media = new TrackMediaStore(options.ResolveDataDir());

        var readCsb = new SqliteConnectionStringBuilder
        {
            DataSource = options.GetFullDbPath(),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
            DefaultTimeout = options.BusyTimeoutSeconds
        };
        _readConnectionString = readCsb.ToString();
    }

    public SqliteStorageOptions Options => _options;

    public SqliteJsonUpcaster Upcaster => _upcaster;

    public string DatabaseId
    {
        get
        {
            EnsureInitialized();
            return _databaseId;
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(SqliteTrackStore));
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_options.ResolveDataDir());
            var fullDbPath = _options.GetFullDbPath();
            var dbExistedBeforeInit = File.Exists(fullDbPath);
            await _migrator.MigrateAsync(fullDbPath, ct).ConfigureAwait(false);
            await _writer.InitializeAsync(ct).ConfigureAwait(false);
            await _writer.EnqueueNonTransactionalAsync<object?>(async (conn, innerCt) =>
            {
                await Media.SweepDeletedTracksAsync(conn, innerCt).ConfigureAwait(false);
                return null;
            }, ct).ConfigureAwait(false);
            _databaseId = ResolveOrCreateDatabaseId(fullDbPath, dbExistedBeforeInit);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    internal async Task<SqliteConnection> OpenReadConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_readConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var busyMs = Math.Max(1, _options.BusyTimeoutSeconds) * 1000;
            await conn.ExecuteAsync(
                new CommandDefinition(
                    $"PRAGMA busy_timeout = {busyMs}; PRAGMA query_only = ON; PRAGMA temp_store = MEMORY;",
                    cancellationToken: ct)).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal TrackMediaStore Media { get; }

    internal async Task<TrackMedia?> OpenTrackMediaAsync(string trackId, string fileName, CancellationToken ct)
    {
        if (!TrackMediaStore.IsTrackSegment(trackId) ||
            !ImageAttachmentContract.TryParseMediaFileName(fileName, out var contentType)) return null;
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await TrackMediaStore.IsReferencedAsync(conn, null, trackId, fileName, ct).ConfigureAwait(false)
            ? Media.Open(trackId, fileName, contentType) : null;
    }

    internal Task<T> WriteWithMediaAsync<T>(string trackId, IReadOnlyList<ImageMediaWrite> images,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> write, CancellationToken ct) =>
        images.Count == 0 ? Writer.EnqueueAsync(write, ct) :
        Writer.EnqueueNonTransactionalAsync(async (conn, innerCt) =>
        {
            var created = new List<string>(images.Count);
            try
            {
                await using var tx = conn.BeginTransaction(deferred: false);
                // Check SQL admission before touching CAS. Both the message and refs stay invisible until commit.
                var result = await write(conn, tx, innerCt).ConfigureAwait(false);
                await Media.SaveAsync(trackId, images, created, innerCt).ConfigureAwait(false);
                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return result;
            }
            catch
            {
                await Media.CleanupUncommittedAsync(conn, trackId, created).ConfigureAwait(false);
                throw;
            }
        }, ct);

    internal void DeleteTrackFiles(string trackId)
    {
        try { ColdBlobs.DeleteTrackBlobsDirectory(trackId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not clean up blobs for deleted Track {0}: {1}", trackId, ex.Message);
        }
        Media.DeleteTrack(trackId);
    }

    internal void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, typeof(SqliteTrackStore));
        if (!_initialized)
        {
            throw new InvalidOperationException("SqliteTrackStore has not been initialized. Call InitializeAsync first.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _writer.DisposeAsync().ConfigureAwait(false);

        using (var poolClearConn = new SqliteConnection(_readConnectionString))
        {
            SqliteConnection.ClearPool(poolClearConn);
        }

        _initLock.Dispose();
    }

    private static string ResolveOrCreateDatabaseId(string fullDbPath, bool dbExistedBeforeInit)
    {
        var idFilePath = fullDbPath + ".id";
        if (dbExistedBeforeInit && File.Exists(idFilePath))
        {
            try
            {
                var existing = File.ReadAllText(idFilePath, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }
            catch (IOException)
            {
                // Fall through to mint a new database ID.
            }
        }

        var created = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(idFilePath, created, Encoding.UTF8);
        }
        catch (IOException)
        {
            // Best-effort persistence on read-only filesystems.
        }

        return created;
    }

    internal SqliteSingleWriter Writer => _writer;

    internal ColdBlobStore ColdBlobs => _coldBlobStore;

    internal TimeProvider TimeProvider => _timeProvider;
}
