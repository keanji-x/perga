using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Perga.Storage.Sqlite;

/// <summary>
/// Represents a versioned, immutable SQL migration script with its SHA-256 integrity checksum.
/// </summary>
public sealed record SqliteMigration(
    int Version,
    string Name,
    string Sql,
    string ChecksumSha256)
{
    public static SqliteMigration Create(int version, string name, string sql)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(version, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sql);

        var normalizedBytes = Encoding.UTF8.GetBytes(NormalizeLineEndings(sql));
        var checksum = Convert.ToHexStringLower(SHA256.HashData(normalizedBytes));
        return new SqliteMigration(version, name, NormalizeLineEndings(sql), checksum);
    }

    public static SqliteMigration FromUtf8Bytes(int version, string name, ReadOnlySpan<byte> rawBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(version, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Strip UTF-8 BOM if present, and normalize CRLF -> LF so git autocrlf never alters SHA-256.
        if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
        {
            rawBytes = rawBytes[3..];
        }

        var text = NormalizeLineEndings(Encoding.UTF8.GetString(rawBytes));
        var normalizedBytes = Encoding.UTF8.GetBytes(text);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(normalizedBytes));
        return new SqliteMigration(version, name, text, checksum);
    }

    private static string NormalizeLineEndings(string text) =>
        text.Contains('\r', StringComparison.Ordinal)
            ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            : text;
}

/// <summary>
/// Thrown when an already-applied migration in <c>schema_migrations</c> fails SHA-256 verification.
/// </summary>
public sealed class SqliteMigrationTamperedException : InvalidOperationException
{
    public int Version { get; }
    public string? ExpectedChecksum { get; }
    public string? ActualChecksum { get; }

    public SqliteMigrationTamperedException(
        int version,
        string? expectedChecksum,
        string? actualChecksum,
        string message)
        : base(message)
    {
        Version = version;
        ExpectedChecksum = expectedChecksum;
        ActualChecksum = actualChecksum;
    }
}

public sealed class SqliteFutureSchemaException(int databaseVersion, int supportedVersion)
    : InvalidOperationException($"Database schema v{databaseVersion} is newer than supported schema v{supportedVersion}.")
{
    public int DatabaseVersion { get; } = databaseVersion;
    public int SupportedVersion { get; } = supportedVersion;
}

/// <summary>
/// Applies byte-frozen SQLite migrations with SHA-256 tamper verification and automatic
/// <c>{dbPath}.bak-v{current}</c> online backup prior to schema upgrades.
/// </summary>
public sealed class SqliteMigrator
{
    public const int CurrentSchemaVersion = 33;

    private readonly IReadOnlyList<SqliteMigration> _migrations;
    private readonly TimeProvider _timeProvider;

    public SqliteMigrator(
        IReadOnlyList<SqliteMigration>? migrations = null,
        TimeProvider? timeProvider = null)
    {
        var list = (migrations ?? LoadEmbeddedMigrations())
            .OrderBy(static m => m.Version)
            .ToList();

        if (list.Count == 0)
        {
            throw new InvalidOperationException("At least one SQLite migration script is required.");
        }

        for (var i = 0; i < list.Count; i++)
        {
            var expectedVersion = i + 1;
            if (list[i].Version != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"SQLite migrations must have contiguous 1-based versions; expected v{expectedVersion}, got v{list[i].Version} ({list[i].Name}).");
            }
        }

        _migrations = list;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<SqliteMigration> Migrations => _migrations;

    public static IReadOnlyList<SqliteMigration> LoadEmbeddedMigrations(Assembly? assembly = null)
    {
        var asm = assembly ?? typeof(SqliteMigrator).Assembly;
        var resourceNames = asm.GetManifestResourceNames()
            .Where(static n => n.Contains(".Migrations.", StringComparison.Ordinal) &&
                               n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var loaded = new List<SqliteMigration>(resourceNames.Count);
        foreach (var resourceName in resourceNames)
        {
            var markerIdx = resourceName.IndexOf(".Migrations.", StringComparison.Ordinal);
            var fileName = resourceName[(markerIdx + ".Migrations.".Length)..];
            var underscoreIdx = fileName.IndexOf('_', StringComparison.Ordinal);
            if (underscoreIdx <= 0 ||
                !int.TryParse(fileName.AsSpan(0, underscoreIdx), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException(
                    $"Invalid embedded SQLite migration filename '{fileName}'. Expected 'NNNN_name.sql'.");
            }

            using var stream = asm.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing embedded migration stream '{resourceName}'.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            loaded.Add(SqliteMigration.FromUtf8Bytes(version, fileName, ms.ToArray()));
        }

        loaded.Sort(static (a, b) => a.Version.CompareTo(b.Version));
        return loaded;
    }

    public Task MigrateAsync(string dbPath, CancellationToken ct = default) =>
        EnsureMigratedAsync(dbPath, allowMigrate: true, ct);

    public async Task EnsureMigratedAsync(string dbPath, bool allowMigrate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        var fullDbPath = Path.GetFullPath(dbPath);
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = fullDbPath,
            Mode = allowMigrate ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = allowMigrate ? 30 : 2
        };

        if (!allowMigrate)
        {
            if (!File.Exists(fullDbPath))
                throw new InvalidOperationException("The database does not exist. Start Perga or pass --allow-migrate.");

            await using var reader = new SqliteConnection(csb.ToString());
            await reader.OpenAsync(ct).ConfigureAwait(false);
            await reader.ExecuteAsync(new CommandDefinition(
                "PRAGMA query_only = ON; PRAGMA busy_timeout = 2000;", cancellationToken: ct)).ConfigureAwait(false);
            var version = await ReadAppliedMigrationsAsync(reader, fullDbPath, ct).ConfigureAwait(false);
            if (version < _migrations.Count)
                throw new InvalidOperationException("The database requires migration. Start Perga or pass --allow-migrate.");
            return;
        }

        var parentDir = Path.GetDirectoryName(fullDbPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);
        await using var migrationLock = await AcquireMigrationLockAsync(fullDbPath + ".migrate.lock", ct).ConfigureAwait(false);
        await using var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var currentVersion = await ReadAppliedMigrationsAsync(conn, fullDbPath, ct).ConfigureAwait(false);
        var pending = _migrations.Where(m => m.Version > currentVersion).OrderBy(static m => m.Version).ToList();

        if (pending.Count == 0)
        {
            await EnsureConnectionPragmasAsync(conn, ct).ConfigureAwait(false);
            return;
        }

        // DESIGN.md 3.1: Before applying pending upgrades on an already-initialized DB, create an online backup.
        if (currentVersion >= 1)
        {
            var backupPath = $"{fullDbPath}.bak-v{currentVersion}";
            DeleteBackupArtifactsIfPresent(backupPath);

            var backupCsb = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 30
            };

            await using var backupConn = new SqliteConnection(backupCsb.ToString());
            await backupConn.OpenAsync(ct).ConfigureAwait(false);
            conn.BackupDatabase(backupConn);
            await backupConn.CloseAsync().ConfigureAwait(false);
        }

        foreach (var migration in pending)
        {
            ct.ThrowIfCancellationRequested();
            var (leadingPragmas, transactionalBody, disablesForeignKeys) = SplitLeadingPragmas(migration.Sql);

            if (!string.IsNullOrWhiteSpace(leadingPragmas))
            {
                await conn.ExecuteAsync(new CommandDefinition(leadingPragmas, cancellationToken: ct)).ConfigureAwait(false);
            }

            if (currentVersion == 0 && migration.Version == 1)
            {
                // If an empty 1-page DB existed prior to setting auto_vacuum=INCREMENTAL, VACUUM once while 0 tables exist
                // so page 1 commits auto_vacuum = 2 (INCREMENTAL) before creating tables.
                var autoVacuumMode = await conn.ExecuteScalarAsync<long>(
                    new CommandDefinition("PRAGMA auto_vacuum;", cancellationToken: ct)).ConfigureAwait(false);
                if (autoVacuumMode != 2)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "PRAGMA auto_vacuum = INCREMENTAL; VACUUM; PRAGMA journal_mode = WAL;",
                        cancellationToken: ct)).ConfigureAwait(false);
                }
            }

            if (!disablesForeignKeys)
            {
                await conn.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys = ON;", cancellationToken: ct)).ConfigureAwait(false);
            }

            await using var tx = conn.BeginTransaction(deferred: false);
            try
            {
                if (!string.IsNullOrWhiteSpace(transactionalBody))
                {
                    await conn.ExecuteAsync(
                        new CommandDefinition(transactionalBody, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
                }

                if (disablesForeignKeys)
                {
                    var violations = (await conn.QueryAsync(
                        new CommandDefinition("PRAGMA foreign_key_check;", transaction: tx, cancellationToken: ct)).ConfigureAwait(false)).AsList();
                    if (violations.Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"Migration v{migration.Version} ('{migration.Name}') failed PRAGMA foreign_key_check with {violations.Count} violation(s).");
                    }
                }

                var appliedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO schema_migrations (version, name, checksum_sha256, applied_at_ms)
                        VALUES (@Version, @Name, @ChecksumSha256, @AppliedAtMs);
                        """,
                        new
                        {
                            migration.Version,
                            migration.Name,
                            migration.ChecksumSha256,
                            AppliedAtMs = appliedAtMs
                        },
                        transaction: tx,
                        cancellationToken: ct)).ConfigureAwait(false);

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (disablesForeignKeys)
            {
                await conn.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys = ON;", cancellationToken: ct)).ConfigureAwait(false);
            }

            currentVersion = migration.Version;
        }

        await EnsureConnectionPragmasAsync(conn, ct).ConfigureAwait(false);
    }

    private async Task<int> ReadAppliedMigrationsAsync(SqliteConnection conn, string fullDbPath, CancellationToken ct)
    {
        var hasMigrationsTable = await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';",
                cancellationToken: ct)).ConfigureAwait(false) > 0;

        var appliedRows = hasMigrationsTable
            ? (await conn.QueryAsync<AppliedMigrationRow>(
                new CommandDefinition(
                    "SELECT version AS Version, name AS Name, checksum_sha256 AS ChecksumSha256, applied_at_ms AS AppliedAtMs FROM schema_migrations ORDER BY version ASC;",
                    cancellationToken: ct)).ConfigureAwait(false)).ToList()
            : [];

        var currentVersion = appliedRows.Count == 0 ? 0 : checked((int)appliedRows[^1].Version);
        if (currentVersion > _migrations.Count)
            throw new SqliteFutureSchemaException(currentVersion, _migrations.Count);

        var knownByVersion = _migrations.ToDictionary(static m => m.Version);
        for (var i = 0; i < appliedRows.Count; i++)
        {
            var applied = appliedRows[i];
            var appliedVersion = (int)applied.Version;
            if (!knownByVersion.TryGetValue(appliedVersion, out var expected))
            {
                throw new SqliteMigrationTamperedException(
                    appliedVersion,
                    expectedChecksum: null,
                    actualChecksum: applied.ChecksumSha256,
                    $"Database '{fullDbPath}' contains unknown applied migration v{appliedVersion} ('{applied.Name}').");
            }

            if (appliedVersion != i + 1)
            {
                throw new SqliteMigrationTamperedException(
                    appliedVersion,
                    expected.ChecksumSha256,
                    applied.ChecksumSha256,
                    $"Database '{fullDbPath}' has non-contiguous applied migration history at v{appliedVersion}.");
            }

            if (!string.Equals(applied.ChecksumSha256, expected.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SqliteMigrationTamperedException(
                    appliedVersion,
                    expected.ChecksumSha256,
                    applied.ChecksumSha256,
                    $"SHA-256 checksum mismatch for applied migration v{appliedVersion} ('{expected.Name}'): expected {expected.ChecksumSha256}, found {applied.ChecksumSha256}.");
            }
        }

        return currentVersion;
    }

    internal static async Task<int> ReadVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        var hasHistory = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations');",
            cancellationToken: ct)).ConfigureAwait(false);
        return hasHistory
            ? await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;", cancellationToken: ct)).ConfigureAwait(false)
            : 0;
    }

    private static async Task<FileStream> AcquireMigrationLockAsync(string path, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33 && started.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task EnsureConnectionPragmasAsync(SqliteConnection conn, CancellationToken ct)
    {
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA temp_store = MEMORY;
                """,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    private static void DeleteBackupArtifactsIfPresent(string backupPath)
    {
        foreach (var candidate in new[] { backupPath, $"{backupPath}-wal", $"{backupPath}-shm" })
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    /// <summary>
    /// Separates leading connection-level <c>PRAGMA</c> statements (such as <c>auto_vacuum</c>, <c>journal_mode = WAL</c>,
    /// and <c>foreign_keys</c>, which SQLite requires to execute outside an active transaction) from the transactional DDL/DML body.
    /// </summary>
    internal static (string LeadingPragmas, string TransactionalBody, bool DisablesForeignKeys) SplitLeadingPragmas(string sql)
    {
        var lines = sql.Split('\n');
        var pragmaBuilder = new StringBuilder();
        var bodyStartLine = 0;
        var disablesFk = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                pragmaBuilder.Append(lines[i]).Append('\n');
                bodyStartLine = i + 1;
                continue;
            }

            if (trimmed.StartsWith("PRAGMA ", StringComparison.OrdinalIgnoreCase) &&
                trimmed.EndsWith(';') &&
                trimmed.IndexOf(';', StringComparison.Ordinal) == trimmed.Length - 1)
            {
                pragmaBuilder.Append(lines[i]).Append('\n');
                bodyStartLine = i + 1;

                var compact = trimmed.Replace(" ", "", StringComparison.Ordinal);
                if (compact.StartsWith("PRAGMAforeign_keys=OFF;", StringComparison.OrdinalIgnoreCase) ||
                    compact.StartsWith("PRAGMAforeign_keys=0;", StringComparison.OrdinalIgnoreCase))
                {
                    disablesFk = true;
                }

                continue;
            }

            break;
        }

        var body = bodyStartLine >= lines.Length
            ? string.Empty
            : string.Join('\n', lines[bodyStartLine..]);

        return (pragmaBuilder.ToString().Trim(), body.Trim(), disablesFk);
    }

    private sealed class AppliedMigrationRow
    {
        public long Version { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ChecksumSha256 { get; init; } = string.Empty;
        public long AppliedAtMs { get; init; }
    }
}
