using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>
/// Persists image attachment metadata, enforces quotas, handles crash-safe staged-to-track/session
/// promotion and message binding, and provides batched hydration for transcripts and queues.
/// </summary>
internal sealed class SessionAttachmentsRepository : ISessionAttachmentStore
{
    private readonly SqliteStoreContext _context;

    internal SessionAttachmentsRepository(SqliteStoreContext context)
    {
        _context = context;
        if (context.Options.MaxAttachmentsPerMessage is < 1 or > SqliteStorageOptions.MaxSchemaAttachmentsPerMessage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                context.Options.MaxAttachmentsPerMessage,
                $"MaxAttachmentsPerMessage must be between 1 and {SqliteStorageOptions.MaxSchemaAttachmentsPerMessage} to match the SQLite schema constraint.");
        }

        Limits = new AttachmentUploadLimits(
            MaxFileBytes: context.Options.MaxAttachmentBytes,
            MaxPerMessage: context.Options.MaxAttachmentsPerMessage,
            MaxSessionBytes: context.Options.MaxSessionAttachmentBytes,
            MaxUnownedStagedBytes: context.Options.MaxUnownedStagedAttachmentBytes,
            MaxUnownedStagedCount: context.Options.MaxUnownedStagedAttachmentCount);
    }

    public AttachmentUploadLimits Limits { get; }

    public async Task<StoredSessionAttachment> StoreStagedAsync(
        string? trackId,
        string? sessionId,
        Stream content,
        string? originalFilename,
        CancellationToken ct)
    {
        _context.EnsureInitialized();
        if (sessionId is not null && string.IsNullOrWhiteSpace(trackId))
        {
            throw KernelException.InvalidArgument("Session-scoped attachment requires a trackId.");
        }

        var sanitizedFilename = SanitizeOriginalFilename(originalFilename);
        var written = await _context.AttachmentBlobs
            .WriteStagedAsync(trackId, content, Limits.MaxFileBytes, ct)
            .ConfigureAwait(false);

        try
        {
            var now = _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await _context.Writer.EnqueueAsync(async (conn, tx, innerCt) =>
            {
                if (sessionId is not null)
                {
                    var sessionRow = await conn.QuerySingleOrDefaultAsync<SessionOwnershipRow>(new CommandDefinition(
                        """
                        SELECT s.track_id AS TrackId, s.archived_at_ms AS ArchivedAtMs, t.closed_at_ms AS ClosedAtMs
                        FROM agent_sessions s
                        JOIN tracks t ON t.id = s.track_id
                        WHERE s.id = @SessionId;
                        """,
                        new { SessionId = sessionId }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                    if (sessionRow is null || !string.Equals(sessionRow.TrackId, trackId, StringComparison.Ordinal))
                    {
                        throw KernelException.NotFound($"Session '{sessionId}' was not found.");
                    }

                    if (sessionRow.ClosedAtMs is not null || sessionRow.ArchivedAtMs is not null)
                    {
                        throw KernelException.PreconditionFailed("Reopen the conversation before uploading attachments.");
                    }

                    var currentSessionBytes = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                        "SELECT COALESCE(SUM(size_bytes), 0) FROM session_attachments WHERE session_id = @SessionId;",
                        new { SessionId = sessionId }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                    if (currentSessionBytes + written.SizeBytes > Limits.MaxSessionBytes)
                    {
                        throw KernelException.AttachmentQuotaExceeded(
                            $"Session attachment quota exceeded ({Limits.MaxSessionBytes} bytes max per session).");
                    }
                }
                else
                {
                    if (trackId is not null)
                    {
                        var closedAt = await conn.QuerySingleOrDefaultAsync<TrackStateRow>(new CommandDefinition(
                            "SELECT id AS Id, closed_at_ms AS ClosedAtMs FROM tracks WHERE id = @TrackId;",
                            new { TrackId = trackId }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                        if (closedAt is null)
                        {
                            throw KernelException.NotFound($"Track '{trackId}' not found.");
                        }

                        if (closedAt.ClosedAtMs is not null)
                        {
                            throw KernelException.PreconditionFailed("Cannot upload attachments to a closed Track.");
                        }
                    }

                    var unownedStats = await conn.QuerySingleAsync<UnownedQuotaRow>(new CommandDefinition(
                        """
                        SELECT COUNT(1) AS ItemCount, COALESCE(SUM(size_bytes), 0) AS TotalBytes
                        FROM session_attachments
                        WHERE session_id IS NULL AND status = 'staged';
                        """,
                        transaction: tx, cancellationToken: innerCt)).ConfigureAwait(false);
                    if (unownedStats.ItemCount + 1 > Limits.MaxUnownedStagedCount ||
                        unownedStats.TotalBytes + written.SizeBytes > Limits.MaxUnownedStagedBytes)
                    {
                        throw KernelException.AttachmentQuotaExceeded(
                            $"Staged attachment quota exceeded ({Limits.MaxUnownedStagedCount} items / {Limits.MaxUnownedStagedBytes} bytes max).");
                    }
                }

                await conn.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO session_attachments (
                        id, track_id, session_id, status, content_type, size_bytes,
                        sha256, original_filename, storage_rel_path, created_at_ms, bound_at_ms
                    )
                    VALUES (
                        @Id, @TrackId, @SessionId, 'staged', @ContentType, @SizeBytes,
                        @Sha256, @OriginalFilename, @StorageRelPath, @CreatedAtMs, NULL
                    );
                    """,
                    new
                    {
                        Id = written.AttachmentId,
                        TrackId = trackId,
                        SessionId = sessionId,
                        written.ContentType,
                        written.SizeBytes,
                        written.Sha256,
                        OriginalFilename = sanitizedFilename,
                        written.StorageRelPath,
                        CreatedAtMs = now
                    },
                    transaction: tx,
                    cancellationToken: innerCt)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            return new StoredSessionAttachment(
                written.AttachmentId,
                trackId,
                sessionId,
                "staged",
                written.ContentType,
                written.SizeBytes,
                written.Sha256,
                sanitizedFilename,
                written.StorageRelPath,
                now,
                BoundAtMs: null,
                PhysicalPath: written.PhysicalPath);
        }
        catch
        {
            _context.AttachmentBlobs.DeleteFileBestEffort(written.StorageRelPath);
            throw;
        }
        finally
        {
            _context.AttachmentBlobs.UnpinRelativePath(written.StorageRelPath);
        }
    }

    public async Task<StoredSessionAttachment?> GetForSessionAsync(
        string sessionId,
        string attachmentId,
        CancellationToken ct)
    {
        _context.EnsureInitialized();
        if (string.IsNullOrWhiteSpace(sessionId) || !SessionAttachmentValidation.IsValidAttachmentId(attachmentId))
        {
            return null;
        }

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<AttachmentRow>(new CommandDefinition(
            AttachmentSelect + "\nWHERE id = @Id AND session_id = @SessionId;",
            new { Id = attachmentId, SessionId = sessionId },
            cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : MapStored(row);
    }

    public async Task<StoredSessionAttachment?> GetUnownedStagedAsync(
        string attachmentId,
        CancellationToken ct)
    {
        _context.EnsureInitialized();
        if (!SessionAttachmentValidation.IsValidAttachmentId(attachmentId))
        {
            return null;
        }

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<AttachmentRow>(new CommandDefinition(
            AttachmentSelect + "\nWHERE id = @Id AND status = 'staged' AND session_id IS NULL;",
            new { Id = attachmentId },
            cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : MapStored(row);
    }

    public Task<Stream?> OpenReadStreamAsync(StoredSessionAttachment attachment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_context.AttachmentBlobs.OpenReadStream(attachment.StorageRelPath));
    }

    /// <summary>
    /// Pre-links any unowned <c>_staged</c> blobs into <c>attachments/{trackId}/</c> prior to entering the
    /// SQLite single writer so promotion is crash-safe and rollback cleans up only newly created links.
    /// </summary>
    internal async Task<List<PromotedAttachmentLink>> PrepareStagedLinksForTrackAsync(
        string trackId,
        IReadOnlyList<string>? attachmentIds,
        bool requireUnownedTrack,
        CancellationToken ct)
    {
        var promoted = new List<PromotedAttachmentLink>();
        if (attachmentIds is null || attachmentIds.Count == 0)
        {
            return promoted;
        }

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = (await conn.QueryAsync<AttachmentRow>(new CommandDefinition(
            AttachmentSelect + "\nWHERE id IN @Ids;",
            new { Ids = attachmentIds },
            cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(r => r.Id, StringComparer.Ordinal);

        try
        {
            foreach (var id in attachmentIds)
            {
                if (!rows.TryGetValue(id, out var row))
                {
                    continue; // Authoritative validation runs inside the writer transaction.
                }

                if (row.Status != "staged" || row.SessionId is not null)
                {
                    continue;
                }

                if (requireUnownedTrack && row.TrackId is not null)
                {
                    continue;
                }

                if (row.TrackId is null)
                {
                    var link = _context.AttachmentBlobs.LinkFromStagedToTrack(row.Id, row.StorageRelPath, trackId, row.ContentType);
                    promoted.Add(link);
                }
            }

            return promoted;
        }
        catch
        {
            CleanupRolledBackLinks(promoted);
            UnpinPromotedLinks(promoted);
            throw;
        }
    }

    internal void CommitPromotedLinks(IReadOnlyList<PromotedAttachmentLink> promoted)
    {
        foreach (var link in promoted)
        {
            if (!string.Equals(link.OldStorageRelPath, link.NewStorageRelPath, StringComparison.Ordinal))
            {
                _context.AttachmentBlobs.DeleteFileBestEffort(link.OldStorageRelPath);
            }
        }
    }

    internal void CleanupRolledBackLinks(IReadOnlyList<PromotedAttachmentLink> promoted)
    {
        foreach (var link in promoted)
        {
            if (link.CreatedNew)
            {
                _context.AttachmentBlobs.DeleteFileBestEffort(link.NewStorageRelPath);
            }
        }
    }

    internal void UnpinPromotedLinks(IReadOnlyList<PromotedAttachmentLink> promoted)
    {
        foreach (var link in promoted)
        {
            if (link.Pinned)
            {
                _context.AttachmentBlobs.UnpinRelativePath(link.NewStorageRelPath);
            }
        }
    }

    /// <summary>
    /// Inside an active writer transaction, binds existing session-owned attachments (staged or already bound
    /// for regenerate/re-send) to a newly inserted message ordinal.
    /// </summary>
    internal async Task<IReadOnlyList<StoredSessionAttachment>> BindSessionAttachmentsInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string sessionId,
        long messageOrdinal,
        IReadOnlyList<string>? attachmentIds,
        long nowMs,
        CancellationToken ct)
    {
        if (attachmentIds is null || attachmentIds.Count == 0)
        {
            return [];
        }

        var rows = (await conn.QueryAsync<AttachmentRow>(new CommandDefinition(
            AttachmentSelect + "\nWHERE id IN @Ids;",
            new { Ids = attachmentIds },
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(r => r.Id, StringComparer.Ordinal);

        var result = new List<StoredSessionAttachment>(attachmentIds.Count);
        for (var pos = 0; pos < attachmentIds.Count; pos++)
        {
            var id = attachmentIds[pos];
            if (!rows.TryGetValue(id, out var row) ||
                !string.Equals(row.SessionId, sessionId, StringComparison.Ordinal) ||
                row.Status is not ("staged" or "bound"))
            {
                throw KernelException.InvalidArgument($"Attachment '{id}' is not available for this session.");
            }

            var boundAtMs = row.BoundAtMs;
            if (row.Status == "staged")
            {
                boundAtMs = nowMs;
                var updated = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE session_attachments
                    SET status = 'bound', bound_at_ms = @Now
                    WHERE id = @Id AND session_id = @SessionId AND status = 'staged';
                    """,
                    new { Id = id, SessionId = sessionId, Now = nowMs },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
                if (updated != 1)
                {
                    throw KernelException.InvalidArgument($"Attachment '{id}' could not be bound to this session.");
                }
            }

            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO session_message_attachments (message_ordinal, position, attachment_id)
                VALUES (@MessageOrdinal, @Position, @AttachmentId);
                """,
                new { MessageOrdinal = messageOrdinal, Position = pos, AttachmentId = id },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            result.Add(MapStored(row with { Status = "bound", BoundAtMs = boundAtMs }));
        }

        return result;
    }

    /// <summary>
    /// Inside an active writer transaction for <c>POST /tracks/{trackId}/sessions</c>, promotes staged
    /// unowned attachments (track_id IS NULL or track_id == trackId, session_id IS NULL) into the new
    /// session, binds them, and links them to the first message ordinal.
    /// </summary>
    internal async Task<IReadOnlyList<StoredSessionAttachment>> PromoteAndBindDraftSessionAttachmentsInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string trackId,
        string sessionId,
        long messageOrdinal,
        IReadOnlyList<string>? attachmentIds,
        IReadOnlyList<PromotedAttachmentLink> preparedLinks,
        long nowMs,
        CancellationToken ct)
    {
        if (attachmentIds is null || attachmentIds.Count == 0)
        {
            return [];
        }

        var linkById = preparedLinks.ToDictionary(l => l.AttachmentId, StringComparer.Ordinal);
        var rows = (await conn.QueryAsync<AttachmentRow>(new CommandDefinition(
            AttachmentSelect + "\nWHERE id IN @Ids;",
            new { Ids = attachmentIds },
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(r => r.Id, StringComparer.Ordinal);

        long totalBytes = 0;
        var orderedRows = new List<AttachmentRow>(attachmentIds.Count);
        foreach (var id in attachmentIds)
        {
            if (!rows.TryGetValue(id, out var row) ||
                row.Status != "staged" ||
                row.SessionId is not null ||
                (row.TrackId is not null && !string.Equals(row.TrackId, trackId, StringComparison.Ordinal)))
            {
                throw KernelException.InvalidArgument($"Staged attachment '{id}' is not available for this session.");
            }

            totalBytes += row.SizeBytes;
            orderedRows.Add(row);
        }

        if (totalBytes > Limits.MaxSessionBytes)
        {
            throw KernelException.AttachmentQuotaExceeded(
                $"Session attachment quota exceeded ({Limits.MaxSessionBytes} bytes max per session).");
        }

        var result = new List<StoredSessionAttachment>(orderedRows.Count);
        for (var pos = 0; pos < orderedRows.Count; pos++)
        {
            var row = orderedRows[pos];
            var newRelPath = row.StorageRelPath;
            if (row.TrackId is null)
            {
                if (linkById.TryGetValue(row.Id, out var prepared))
                {
                    newRelPath = prepared.NewStorageRelPath;
                }
                else
                {
                    var linked = _context.AttachmentBlobs.LinkFromStagedToTrack(row.Id, row.StorageRelPath, trackId, row.ContentType, pinTarget: false);
                    newRelPath = linked.NewStorageRelPath;
                }
            }

            var updated = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE session_attachments
                SET track_id = @TrackId,
                    session_id = @SessionId,
                    status = 'bound',
                    bound_at_ms = @Now,
                    storage_rel_path = @StorageRelPath
                WHERE id = @Id AND status = 'staged' AND session_id IS NULL
                  AND (track_id IS NULL OR track_id = @TrackId);
                """,
                new { row.Id, TrackId = trackId, SessionId = sessionId, Now = nowMs, StorageRelPath = newRelPath },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
            if (updated != 1)
            {
                throw KernelException.InvalidArgument($"Staged attachment '{row.Id}' could not be bound to this session.");
            }

            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO session_message_attachments (message_ordinal, position, attachment_id)
                VALUES (@MessageOrdinal, @Position, @AttachmentId);
                """,
                new { MessageOrdinal = messageOrdinal, Position = pos, AttachmentId = row.Id },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

            result.Add(MapStored(row with
            {
                TrackId = trackId,
                SessionId = sessionId,
                Status = "bound",
                BoundAtMs = nowMs,
                StorageRelPath = newRelPath
            }));
        }

        return result;
    }

    /// <summary>
    /// Inside an active writer transaction for <c>POST /tracks</c>, promotes unowned <c>_staged</c>
    /// attachments to the new Track and its Lead session (status remains <c>staged</c> for initial send).
    /// </summary>
    internal async Task AdoptStagedForNewTrackInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string trackId,
        string leadSessionId,
        IReadOnlyList<string>? attachmentIds,
        IReadOnlyList<PromotedAttachmentLink> preparedLinks,
        CancellationToken ct)
    {
        if (attachmentIds is null || attachmentIds.Count == 0)
        {
            return;
        }

        var linkById = preparedLinks.ToDictionary(l => l.AttachmentId, StringComparer.Ordinal);
        var rows = (await conn.QueryAsync<AttachmentRow>(new CommandDefinition(
            AttachmentSelect + "\nWHERE id IN @Ids;",
            new { Ids = attachmentIds },
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(r => r.Id, StringComparer.Ordinal);

        long totalBytes = 0;
        var orderedRows = new List<AttachmentRow>(attachmentIds.Count);
        foreach (var id in attachmentIds)
        {
            if (!rows.TryGetValue(id, out var row) ||
                row.Status != "staged" ||
                row.TrackId is not null ||
                row.SessionId is not null)
            {
                throw KernelException.InvalidArgument($"Staged attachment '{id}' is not available for new Track creation.");
            }

            totalBytes += row.SizeBytes;
            orderedRows.Add(row);
        }

        if (totalBytes > Limits.MaxSessionBytes)
        {
            throw KernelException.AttachmentQuotaExceeded(
                $"Session attachment quota exceeded ({Limits.MaxSessionBytes} bytes max per session).");
        }

        foreach (var row in orderedRows)
        {
            var newRelPath = linkById.TryGetValue(row.Id, out var prepared)
                ? prepared.NewStorageRelPath
                : _context.AttachmentBlobs.LinkFromStagedToTrack(row.Id, row.StorageRelPath, trackId, row.ContentType, pinTarget: false).NewStorageRelPath;

            var updated = await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE session_attachments
                SET track_id = @TrackId,
                    session_id = @SessionId,
                    storage_rel_path = @StorageRelPath
                WHERE id = @Id AND status = 'staged' AND track_id IS NULL AND session_id IS NULL;
                """,
                new { row.Id, TrackId = trackId, SessionId = leadSessionId, StorageRelPath = newRelPath },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
            if (updated != 1)
            {
                throw KernelException.InvalidArgument($"Staged attachment '{row.Id}' could not be adopted by Track '{trackId}'.");
            }
        }
    }

    internal async Task<Dictionary<long, IReadOnlyList<StoredSessionAttachment>>> LoadByMessageOrdinalsAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        IReadOnlyCollection<long> messageOrdinals,
        CancellationToken ct)
    {
        var result = new Dictionary<long, IReadOnlyList<StoredSessionAttachment>>();
        if (messageOrdinals.Count == 0)
        {
            return result;
        }

        var rows = await conn.QueryAsync<MessageAttachmentRow>(new CommandDefinition(
            """
            SELECT sma.message_ordinal AS MessageOrdinal,
                   a.id AS Id,
                   a.track_id AS TrackId,
                   a.session_id AS SessionId,
                   a.status AS Status,
                   a.content_type AS ContentType,
                   a.size_bytes AS SizeBytes,
                   a.sha256 AS Sha256,
                   a.original_filename AS OriginalFilename,
                   a.storage_rel_path AS StorageRelPath,
                   a.created_at_ms AS CreatedAtMs,
                   a.bound_at_ms AS BoundAtMs
            FROM session_message_attachments sma
            JOIN session_attachments a ON a.id = sma.attachment_id
            WHERE sma.message_ordinal IN @Ordinals
            ORDER BY sma.message_ordinal ASC, sma.position ASC;
            """,
            new { Ordinals = messageOrdinals },
            transaction: tx,
            cancellationToken: ct)).ConfigureAwait(false);

        foreach (var group in rows.GroupBy(static r => r.MessageOrdinal))
        {
            result[group.Key] = group.Select(MapStored).ToArray();
        }

        return result;
    }

    internal async Task<IReadOnlyList<StoredSessionAttachment>> LoadOrderedUnionForOrdinalsAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        IReadOnlyList<long> messageOrdinals,
        CancellationToken ct)
    {
        if (messageOrdinals.Count == 0)
        {
            return [];
        }

        var byOrdinal = await LoadByMessageOrdinalsAsync(conn, tx, messageOrdinals, ct).ConfigureAwait(false);
        return CombineOrderedUnique(messageOrdinals, byOrdinal);
    }

    internal static IReadOnlyList<StoredSessionAttachment> CombineOrderedUnique(
        IEnumerable<long> orderedOrdinals,
        IReadOnlyDictionary<long, IReadOnlyList<StoredSessionAttachment>> byOrdinal)
    {
        List<StoredSessionAttachment>? combined = null;
        HashSet<string>? seen = null;
        foreach (var ordinal in orderedOrdinals)
        {
            if (!byOrdinal.TryGetValue(ordinal, out var list))
            {
                continue;
            }

            foreach (var att in list)
            {
                seen ??= new HashSet<string>(StringComparer.Ordinal);
                if (seen.Add(att.Id))
                {
                    combined ??= [];
                    combined.Add(att);
                }
            }
        }

        return combined is { Count: > 0 } ? combined : [];
    }

    private StoredSessionAttachment MapStored(AttachmentRow r) =>
        new(
            r.Id,
            r.TrackId,
            r.SessionId,
            r.Status,
            r.ContentType,
            r.SizeBytes,
            r.Sha256,
            r.OriginalFilename,
            r.StorageRelPath,
            r.CreatedAtMs,
            r.BoundAtMs,
            PhysicalPath: _context.AttachmentBlobs.TryResolvePhysicalPath(r.StorageRelPath));

    private static string? SanitizeOriginalFilename(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = Path.GetFileName(raw.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var cleaned = new string(trimmed.Where(static c => !char.IsControl(c) && c is not ('/' or '\\')).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length > 255 ? cleaned[..255] : cleaned;
    }

    private const string AttachmentSelect = """
        SELECT id AS Id,
               track_id AS TrackId,
               session_id AS SessionId,
               status AS Status,
               content_type AS ContentType,
               size_bytes AS SizeBytes,
               sha256 AS Sha256,
               original_filename AS OriginalFilename,
               storage_rel_path AS StorageRelPath,
               created_at_ms AS CreatedAtMs,
               bound_at_ms AS BoundAtMs
        FROM session_attachments
        """;

    private sealed record SessionOwnershipRow(string TrackId, long? ArchivedAtMs, long? ClosedAtMs);
    private sealed record TrackStateRow(string Id, long? ClosedAtMs);
    private sealed record UnownedQuotaRow(long ItemCount, long TotalBytes);

    private record AttachmentRow
    {
        public string Id { get; init; } = string.Empty;
        public string? TrackId { get; init; }
        public string? SessionId { get; init; }
        public string Status { get; init; } = string.Empty;
        public string ContentType { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string Sha256 { get; init; } = string.Empty;
        public string? OriginalFilename { get; init; }
        public string StorageRelPath { get; init; } = string.Empty;
        public long CreatedAtMs { get; init; }
        public long? BoundAtMs { get; init; }
    }

    private sealed record MessageAttachmentRow : AttachmentRow
    {
        public long MessageOrdinal { get; init; }
    }
}
