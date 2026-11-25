using Dapper;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Ask and mail persistence, including delivery wakes.</summary>
internal sealed class CorrespondenceRepository
{
    private readonly SqliteStoreContext _context;

    internal CorrespondenceRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public Task UpsertAskAsync(AskDto ask, EventEnvelopeDto evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(evt);
        _context.EnsureInitialized();

        var status = SqliteJsonUpcaster.FormatAskStatus(ask.Status);
        var questionsJson = SqliteJsonUpcaster.SerializeAskQuestions(ask.Questions);
        var answerJson = SqliteJsonUpcaster.SerializeNullableElement(ask.Answer);

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO asks (id, track_id, status, questions_json, answer_json, created_at_ms, answered_at_ms, delivery, session_id, turn_id, target_session_id)
                        VALUES (@Id, @TrackId, @Status, @QuestionsJson, @AnswerJson, @CreatedAtMs, @AnsweredAtMs, @Delivery, @SessionId, @TurnId, @TargetSessionId)
                        ON CONFLICT(id) DO UPDATE SET
                            status = excluded.status,
                            questions_json = excluded.questions_json,
                            answer_json = excluded.answer_json,
                            answered_at_ms = excluded.answered_at_ms;
                        """,
                        new
                        {
                            ask.Id,
                            ask.TrackId,
                            Status = status,
                            QuestionsJson = questionsJson,
                            AnswerJson = answerJson,
                            ask.CreatedAtMs,
                            ask.AnsweredAtMs,
                            Delivery = ask.Delivery.ToString().ToLowerInvariant(),
                            ask.SessionId,
                            ask.TurnId, ask.TargetSessionId
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                if (ask.Delivery == AskDelivery.Wake && ask.AnsweredAtMs is not null)
                    await TrackWakesRepository.EnqueueWakeOnceInternalAsync(conn, tx, TrackWakeRequests.Ask(ask), ask.AnsweredAtMs.Value, innerCt).ConfigureAwait(false);
                await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
            },
            ct);
    }

    public async Task<AskDto?> GetAskAsync(string askId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(askId);
        _context.EnsureInitialized();

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<AskRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       track_id AS TrackId,
                       status AS Status,
                       questions_json AS QuestionsJson,
                       answer_json AS AnswerJson,
                       created_at_ms AS CreatedAtMs,
                       answered_at_ms AS AnsweredAtMs,
                       delivery AS Delivery, session_id AS SessionId, turn_id AS TurnId, target_session_id AS TargetSessionId
                FROM asks
                WHERE id = @AskId;
                """,
                new { AskId = askId },
                cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : MapAsk(row);
    }

    public Task<MailDto> UpsertMailAsync(MailDto mail, EventEnvelopeDto evt, CancellationToken ct, bool requestWake = true, Action? beforeWrite = null, ResourceCreationKey? creationKey = null)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(evt);
        _context.EnsureInitialized();

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                if (creationKey is not null &&
                    await CreationRepository.FindCreationInternalAsync(conn, tx, creationKey, innerCt).ConfigureAwait(false) is { } replayId)
                {
                    var row = await conn.QuerySingleOrDefaultAsync<MailRow>(new CommandDefinition(
                        MailSelect + " WHERE id = @Id;", new { Id = replayId }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                    return row is not null
                        ? MapMail(row)
                        : throw new KernelException(KernelErrorCode.IdempotencyKeyExhausted, "The created mail was deleted.");
                }

                var isNew = !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS(SELECT 1 FROM mails WHERE id=@Id);", new { mail.Id }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                beforeWrite?.Invoke();
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO mails (id, from_track_id, to_track_id, summary, body, read_at_ms, created_at_ms, reply_to_mail_id, wake_pending)
                        VALUES (@Id, @FromTrackId, @ToTrackId, @Summary, @Body, @ReadAtMs, @CreatedAtMs, @ReplyToMailId, 0)
                        ON CONFLICT(id) DO UPDATE SET
                            summary = excluded.summary,
                            body = excluded.body,
                            read_at_ms = excluded.read_at_ms,
                            wake_pending = 0;
                        """,
                        new
                        {
                            mail.Id,
                            RequestWake = requestWake,
                            mail.ReplyToMailId,
                            mail.FromTrackId,
                            mail.ToTrackId,
                            mail.Summary,
                            mail.Body,
                            mail.ReadAtMs,
                            mail.CreatedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                if (requestWake && isNew)
                    await TrackWakesRepository.EnqueueWakeOnceInternalAsync(conn, tx, TrackWakeRequests.Mail(mail), mail.CreatedAtMs, innerCt).ConfigureAwait(false);
                await CreationRepository.InsertResourceReceiptAsync(conn, tx, creationKey, mail.Id, mail.CreatedAtMs, null, innerCt).ConfigureAwait(false);
                await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                return mail;
            },
            ct);
    }

    public Task MarkMailReadAsync(string mailId, long readAtMs, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailId);
        _context.EnsureInitialized();

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE mails SET read_at_ms = COALESCE(read_at_ms, @ReadAtMs) WHERE id = @MailId;",
                        new { MailId = mailId, ReadAtMs = readAtMs },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);
            },
            ct);
    }

    public Task AcknowledgeMailWakeAsync(string mailId, CancellationToken ct) =>
        _context.Writer.EnqueueAsync(async (conn, tx, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"""
                UPDATE mails SET wake_pending = 0 WHERE id = @mailId;
                UPDATE system_upgrade_receipts SET notification_status = CASE
                    WHEN EXISTS (SELECT 1 FROM mails m JOIN tracks t ON t.id = m.to_track_id
                        WHERE m.id = @mailId AND t.closed_at_ms IS NULL) THEN 'notified' ELSE 'target_unavailable' END
                    WHERE '{TrackWakeRequests.SystemUpgradeMailIdPrefix}' || job_id = @mailId;
                """,
                new { mailId }, tx, cancellationToken: token)).ConfigureAwait(false);
        }, ct);

    private const string MailSelect = """
        SELECT id AS Id, from_track_id AS FromTrackId, to_track_id AS ToTrackId,
               summary AS Summary, body AS Body, read_at_ms AS ReadAtMs,
               created_at_ms AS CreatedAtMs, reply_to_mail_id AS ReplyToMailId
        FROM mails
        """;

    public async Task<MailDto?> FindReceivedMailAsync(string trackId, string mailId, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<MailRow>(new CommandDefinition(
            MailSelect + " WHERE id = @mailId AND to_track_id = @trackId;",
            new { trackId, mailId }, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : MapMail(row);
    }

    public Task<IReadOnlyList<MailDto>> ListMailsAsync(string trackId, bool unreadOnly = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        var filter = unreadOnly ? " AND read_at_ms IS NULL" : "";
        return QueryMailsAsync(" WHERE to_track_id = @trackId" + filter + " ORDER BY created_at_ms DESC, id;", new { trackId }, ct);
    }

    private async Task<IReadOnlyList<MailDto>> QueryMailsAsync(string clause, object? parameters, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<MailRow>(new CommandDefinition(MailSelect + clause,
            parameters, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(MapMail).ToList();
    }

    internal static AskDto MapAsk(AskRow r) =>
        new(
            Id: r.Id,
            TrackId: r.TrackId,
            Status: SqliteJsonUpcaster.ParseAskStatus(r.Status),
            Questions: SqliteJsonUpcaster.DeserializeAskQuestions(r.QuestionsJson),
            Answer: SqliteJsonUpcaster.ParseNullableElement(r.AnswerJson),
            CreatedAtMs: r.CreatedAtMs,
            AnsweredAtMs: r.AnsweredAtMs,
            Delivery: r.Delivery switch { "hold" => AskDelivery.Hold, "wake" => AskDelivery.Wake, _ => throw new InvalidOperationException("Invalid ask delivery.") },
            SessionId: r.SessionId,
            TurnId: r.TurnId, TargetSessionId: r.TargetSessionId);

    private static MailDto MapMail(MailRow r) =>
        new(
            Id: r.Id,
            FromTrackId: r.FromTrackId,
            ToTrackId: r.ToTrackId,
            Summary: r.Summary,
            Body: r.Body,
            ReadAtMs: r.ReadAtMs,
            CreatedAtMs: r.CreatedAtMs,
            ReplyToMailId: r.ReplyToMailId);

    internal sealed class AskRow
    {
        public string? TargetSessionId { get; init; }
        public string Delivery { get; init; } = string.Empty;
        public string? SessionId { get; init; }
        public string? TurnId { get; init; }
        public string Id { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string Status { get; init; } = "open";
        public string QuestionsJson { get; init; } = "[]";
        public string? AnswerJson { get; init; }
        public long CreatedAtMs { get; init; }
        public long? AnsweredAtMs { get; init; }
    }

    private sealed class MailRow
    {
        public string? ReplyToMailId { get; init; }
        public string Id { get; init; } = string.Empty;
        public string FromTrackId { get; init; } = string.Empty;
        public string ToTrackId { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public long? ReadAtMs { get; init; }
        public long CreatedAtMs { get; init; }
    }
}
