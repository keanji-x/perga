using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class SessionAttachmentStorageTests
{
    private const long DayMs = 86_400_000L;

    internal static readonly byte[] ValidPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
    ];

    internal static readonly byte[] ValidJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    internal static readonly byte[] ValidGif87a = "GIF87a\x01\x00\x01\x00\x00\x00"u8.ToArray();
    internal static readonly byte[] ValidGif89a = "GIF89a\x01\x00\x01\x00\x00\x00"u8.ToArray();
    internal static readonly byte[] ValidWebP = [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20];

    [Fact]
    public async Task MagicByteSniffing_AcceptsSupportedFormats_AndRejectsUnsafeOrCorruptPayloads()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var attachments = Assert.IsAssignableFrom<ISessionAttachmentStore>(store.SessionAttachments);

        foreach (var (payload, expectedMime, expectedExt) in new[]
        {
            (ValidPng, "image/png", ".png"),
            (ValidJpeg, "image/jpeg", ".jpg"),
            (ValidGif87a, "image/gif", ".gif"),
            (ValidGif89a, "image/gif", ".gif"),
            (ValidWebP, "image/webp", ".webp"),
        })
        {
            await using var ms = new MemoryStream(payload);
            var stored = await attachments.StoreStagedAsync(trackId: null, sessionId: null, ms, "photo.exe", CancellationToken.None);
            Assert.Equal(expectedMime, stored.ContentType);
            Assert.Equal(payload.Length, stored.SizeBytes);
            Assert.EndsWith(expectedExt, stored.StorageRelPath, StringComparison.Ordinal);
            Assert.StartsWith("attachments/_staged/att_", stored.StorageRelPath, StringComparison.Ordinal);
            Assert.True(File.Exists(stored.PhysicalPath));
        }

        byte[][] rejected =
        [
            [],
            Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"),
            Encoding.UTF8.GetBytes("<html><body>hi</body></html>"),
            Encoding.UTF8.GetBytes("RIFF\x00\x00\x00\x00AVI "),
            [0x42, 0x4D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00] // BMP
        ];

        foreach (var bad in rejected)
        {
            await using var ms = new MemoryStream(bad);
            var ex = await Assert.ThrowsAsync<KernelException>(() =>
                attachments.StoreStagedAsync(null, null, ms, "bad.png", CancellationToken.None));
            Assert.Equal(KernelErrorCode.InvalidArgument, ex.Code);
        }

        // Zero temp files left behind under attachments/_staged
        var stagedDir = Path.Combine(harness.DataDir, "attachments", "_staged");
        Assert.Empty(Directory.GetFiles(stagedDir, ".*.tmp.*"));
    }

    [Fact]
    public async Task StreamingSizeLimit_AndQuotas_RejectAndDeleteTempOrOrphanFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-att-quota-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, "perga.db");
            await using var store = new SqliteTrackStore(new SqliteStorageOptions
            {
                DbPath = dbPath,
                DataDir = tempDir,
                MaxAttachmentBytes = 64,
                MaxSessionAttachmentBytes = 90,
                MaxUnownedStagedAttachmentBytes = 80,
                MaxUnownedStagedAttachmentCount = 2
            });
            await store.InitializeAsync();
            var ct = CancellationToken.None;
            await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_1"), ct);
            var (track, report, lead) = StorageTestHarness.CreateSeedTrack("trk_q", "area_1", "ses_q");
            await store.CreateTrackAsync(track, report, null, lead, ct);
            var attachments = store.SessionAttachments!;

            // 1. Over single-file byte limit -> PayloadTooLarge and no files left behind
            var oversizePng = ValidPng.Concat(new byte[100]).ToArray();
            await using (var ms = new MemoryStream(oversizePng))
            {
                var ex = await Assert.ThrowsAsync<KernelException>(() =>
                    attachments.StoreStagedAsync("trk_q", "ses_q", ms, "big.png", ct));
                Assert.Equal(KernelErrorCode.PayloadTooLarge, ex.Code);
            }

            // 2. Session byte quota: 50 bytes ok, second 50 bytes exceeds 90-byte session cap -> AttachmentQuotaExceeded + file cleaned up
            var png50 = ValidPng.Concat(new byte[50 - ValidPng.Length]).ToArray();
            await using (var ms1 = new MemoryStream(png50))
            {
                var first = await attachments.StoreStagedAsync("trk_q", "ses_q", ms1, "1.png", ct);
                Assert.Equal(50, first.SizeBytes);
            }
            await using (var ms2 = new MemoryStream(png50))
            {
                var ex = await Assert.ThrowsAsync<KernelException>(() =>
                    attachments.StoreStagedAsync("trk_q", "ses_q", ms2, "2.png", ct));
                Assert.Equal(KernelErrorCode.AttachmentQuotaExceeded, ex.Code);
            }
            Assert.Single(Directory.GetFiles(Path.Combine(tempDir, "attachments", "trk_q")));

            // 3. Unowned staged count quota (max 2)
            for (var i = 0; i < 2; i++)
            {
                await using var ms = new MemoryStream(ValidPng);
                await attachments.StoreStagedAsync(null, null, ms, $"u{i}.png", ct);
            }
            await using (var ms3 = new MemoryStream(ValidPng))
            {
                var ex = await Assert.ThrowsAsync<KernelException>(() =>
                    attachments.StoreStagedAsync(null, null, ms3, "u3.png", ct));
                Assert.Equal(KernelErrorCode.AttachmentQuotaExceeded, ex.Code);
            }
            Assert.Equal(2, Directory.GetFiles(Path.Combine(tempDir, "attachments", "_staged")).Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PathTraversalAndSymlinks_AreRejected_AndDbTriggerBlocksCrossSessionLinks()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;
        var attachments = store.SessionAttachments!;

        await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_1"), ct);
        var (t1, r1, s1) = StorageTestHarness.CreateSeedTrack("trk_1", "area_1", "ses_1");
        await store.CreateTrackAsync(t1, r1, null, s1, ct);
        var (t2, r2, s2) = StorageTestHarness.CreateSeedTrack("trk_2", "area_1", "ses_2");
        await store.CreateTrackAsync(t2, r2, null, s2, ct);

        await using var msBadTrack = new MemoryStream(ValidPng);
        var traversalEx = await Assert.ThrowsAsync<KernelException>(() =>
            attachments.StoreStagedAsync("../escape", null, msBadTrack, "x.png", ct));
        Assert.Equal(KernelErrorCode.InvalidArgument, traversalEx.Code);

        await using var ms = new MemoryStream(ValidPng);
        var att = await attachments.StoreStagedAsync("trk_1", "ses_1", ms, "img.png", ct);

        // Symlink replacement on disk must be refused by OpenReadStreamAsync
        if (!OperatingSystem.IsWindows())
        {
            var outsideFile = Path.Combine(harness.DataDir, "secret.txt");
            await File.WriteAllTextAsync(outsideFile, "top-secret");
            File.Delete(att.PhysicalPath!);
            File.CreateSymbolicLink(att.PhysicalPath!, outsideFile);
            Assert.Null(await attachments.OpenReadStreamAsync(att, ct));
        }

        // Cross-session attachment bind via AcceptAsync must fail cleanly
        var sessionEvent = new EventEnvelopeDto(null, "area_1", "trk_2", "session.updated", StorageTestHarness.Json("{}"), 2_000);
        var crossEx = await Assert.ThrowsAsync<KernelException>(() =>
            store.SessionInputs.AcceptAsync(
                s2 with { Status = "queued", UpdatedAtMs = 2_000 },
                new SessionInputRequest("k1", "h1", "", "owner:installation", [att.Id]),
                sessionEvent,
                ct));
        Assert.Equal(KernelErrorCode.InvalidArgument, crossEx.Code);

        // Direct SQL attempt bypassing repo must also be aborted by trigger session_message_attachments_same_session
        await store.SessionInputs.AcceptAsync(
            s2 with { Status = "queued", UpdatedAtMs = 2_000 },
            new SessionInputRequest("k2", "h2", "hi"),
            sessionEvent,
            ct);
        using var rawConn = harness.OpenRawInspectConnection();
        var s2Ordinal = await rawConn.ExecuteScalarAsync<long>("SELECT ordinal FROM session_messages WHERE session_id = 'ses_2' LIMIT 1;");
        var sqliteEx = await Assert.ThrowsAsync<SqliteException>(() => rawConn.ExecuteAsync(
            "INSERT INTO session_message_attachments (message_ordinal, position, attachment_id) VALUES (@Ord, 0, @Id);",
            new { Ord = s2Ordinal, att.Id }));
        Assert.Contains("attachment does not belong to message session or is not bound", sqliteEx.Message);
    }

    [Fact]
    public async Task CrashSafePromotion_QueuedBatchUnion_RestartDurability_TrackDeletionAndMaintenanceGc()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-att-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "perga.db");
        const long baseNowMs = 100L * DayMs;

        try
        {
            string track1Id = "trk_life";
            string lead1Id = "ses_lead";
            string draftSessionId = "ses_draft";
            string attNewTrackId;
            string attDraftId;
            string attQ1Id;
            string attQ2Id;

            // Phase 1: create tracks, promote staged attachments, queue multiple image messages, persist state
            await using (var store = new SqliteTrackStore(new SqliteStorageOptions { DbPath = dbPath, DataDir = tempDir }))
            {
                await store.InitializeAsync();
                var ct = CancellationToken.None;
                var attachments = store.SessionAttachments!;
                await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_1"), ct);

                // Upload unowned _staged image -> adopt on CreateTrackAsync
                await using (var ms = new MemoryStream(ValidPng))
                {
                    var staged = await attachments.StoreStagedAsync(null, null, ms, "track.png", ct);
                    attNewTrackId = staged.Id;
                    Assert.True(File.Exists(Path.Combine(tempDir, "attachments", "_staged", $"{attNewTrackId}.png")));
                }

                // Failed CreateTrackAsync (missing area) must roll back newly created link and keep _staged intact!
                var (badTrack, badRep, badLead) = StorageTestHarness.CreateSeedTrack("trk_bad", "area_missing", "ses_bad");
                await Assert.ThrowsAsync<KernelException>(() => store.CreateTrackAsync(
                    new TrackCreation(badTrack, badRep, null, badLead, [], null, null, []) { AttachmentIds = [attNewTrackId] },
                    null, ct));
                Assert.False(File.Exists(Path.Combine(tempDir, "attachments", "trk_bad", $"{attNewTrackId}.png")));
                Assert.True(File.Exists(Path.Combine(tempDir, "attachments", "_staged", $"{attNewTrackId}.png")));

                // Valid CreateTrackAsync promotes blob from _staged -> {track1Id} and adopts to lead session (status=staged)
                var (track, report, lead) = StorageTestHarness.CreateSeedTrack(track1Id, "area_1", lead1Id, nowMs: baseNowMs);
                await store.CreateTrackAsync(
                    new TrackCreation(track, report, null, lead, [], null, null, []) { AttachmentIds = [attNewTrackId] },
                    null, ct);
                Assert.False(File.Exists(Path.Combine(tempDir, "attachments", "_staged", $"{attNewTrackId}.png")));
                Assert.True(File.Exists(Path.Combine(tempDir, "attachments", track1Id, $"{attNewTrackId}.png")));

                // Bind adopted image via image-only AcceptAsync on lead session
                var evt = new EventEnvelopeDto(null, "area_1", track1Id, "session.updated", StorageTestHarness.Json("{}"), baseNowMs + 10);
                var accepted = await store.SessionInputs.AcceptAsync(
                    lead with { Status = "queued", UpdatedAtMs = baseNowMs + 10 },
                    new SessionInputRequest("k-lead-1", "h-lead-1", "", "owner:installation", [attNewTrackId]),
                    evt,
                    ct);
                Assert.Single(accepted.Attachments!);
                Assert.Equal(attNewTrackId, accepted.Attachments![0].Id);

                // Upload unowned staged image for New Session draft -> promote & bind on SessionInputs.CreateAsync
                await using (var ms = new MemoryStream(ValidWebP))
                {
                    var draftStaged = await attachments.StoreStagedAsync(null, null, ms, "draft.webp", ct);
                    attDraftId = draftStaged.Id;
                }
                var draftSession = new AgentSessionDto(
                    draftSessionId, track1Id, false, "Draft", "codex", null, null, 1, "queued", null, baseNowMs + 20, baseNowMs + 20);
                var createdInput = await store.SessionInputs.CreateAsync(
                    new SessionCreateIntent(track1Id, "k-draft", "h-draft"),
                    draftSession,
                    new SessionInputRequest("k-draft", "h-draft-1", "", "owner:installation", [attDraftId]),
                    evt,
                    ct);
                Assert.NotNull(createdInput);
                Assert.Single(createdInput!.Attachments!);
                Assert.Equal(attDraftId, createdInput.Attachments![0].Id);
                Assert.False(File.Exists(Path.Combine(tempDir, "attachments", "_staged", $"{attDraftId}.webp")));
                Assert.True(File.Exists(Path.Combine(tempDir, "attachments", track1Id, $"{attDraftId}.webp")));
                // Bound attachment is no longer readable as unowned staged
                Assert.Null(await attachments.GetUnownedStagedAsync(attDraftId, ct));

                // Queue 2 messages (one image-only, one text + overlapping & new image) and promote batch
                await using (var ms1 = new MemoryStream(ValidJpeg))
                {
                    attQ1Id = (await attachments.StoreStagedAsync(track1Id, lead1Id, ms1, "q1.jpg", ct)).Id;
                }
                await using (var ms2 = new MemoryStream(ValidGif89a))
                {
                    attQ2Id = (await attachments.StoreStagedAsync(track1Id, lead1Id, ms2, "q2.gif", ct)).Id;
                }

                var qMsg1 = new SessionMessage("msg_q1", lead1Id, "owner:installation", "kq1", "hq1", null, null, "", "queued", baseNowMs + 30, "queue")
                {
                    AttachmentIds = [attQ1Id]
                };
                var qMsg2 = new SessionMessage("msg_q2", lead1Id, "owner:installation", "kq2", "hq2", null, null, "check both", "queued", baseNowMs + 31, "queue")
                {
                    AttachmentIds = [attQ1Id, attQ2Id] // overlaps attQ1Id (bound reuse) + attQ2Id
                };
                await store.SessionMessages.InsertAsync(qMsg1, ct);
                await store.SessionMessages.InsertAsync(qMsg2, ct);

                var queuedList = await store.SessionMessages.ListQueueAsync(lead1Id, ct);
                Assert.Equal(2, queuedList.Count);
                Assert.Equal([attQ1Id], queuedList[0].Attachments!.Select(a => a.Id));
                Assert.Equal([attQ1Id, attQ2Id], queuedList[1].Attachments!.Select(a => a.Id));

                // Mark lead idle and promote queued batch -> combined prompt has no leading blank line and ordered unique attachments [attQ1Id, attQ2Id]
                await store.UpsertSessionAsync(lead with { Status = "idle", UpdatedAtMs = baseNowMs + 40 }, null, ct);
                var promoted = await store.SessionInputs.PromoteQueuedBatchAsync(
                    lead with { Status = "queued", UpdatedAtMs = baseNowMs + 41 }, qMsg1, evt, ct);
                Assert.NotNull(promoted);
                Assert.Equal("check both", promoted!.Content);
                Assert.Equal([attQ1Id, attQ2Id], promoted.Attachments!.Select(a => a.Id));
            }

            // Phase 2: Reopen SAME SQLite DB in a fresh SqliteTrackStore to verify full restart durability!
            await using (var reopened = new SqliteTrackStore(new SqliteStorageOptions { DbPath = dbPath, DataDir = tempDir }))
            {
                await reopened.InitializeAsync();
                var ct = CancellationToken.None;

                var leadItems = await reopened.ListSessionItemsAsync(lead1Id, 0, 20, ct);
                var userItems = leadItems.Where(i => i.Kind == "user").ToList();
                Assert.Equal(3, userItems.Count);
                Assert.Equal([attNewTrackId], userItems[0].Attachments!.Select(a => a.Id));
                Assert.Equal([attQ1Id], userItems[1].Attachments!.Select(a => a.Id));
                Assert.Equal([attQ1Id, attQ2Id], userItems[2].Attachments!.Select(a => a.Id));

                var recovered = await reopened.RecoverSessionInputsAsync(ct);
                var recoveredDraft = Assert.Single(recovered, r => r.Input.Receipt.SessionId == draftSessionId);
                Assert.Equal([attDraftId], recoveredDraft.Input.Attachments!.Select(a => a.Id));
                Assert.True(File.Exists(recoveredDraft.Input.Attachments![0].PhysicalPath));

                // Phase 3: Maintenance GC (expired staged row > 24h + old unreferenced disk orphan > 10m; fresh staged & bound preserved)
                var attachments = reopened.SessionAttachments!;
                string expiredStagedId;
                await using (var ms = new MemoryStream(ValidPng))
                {
                    expiredStagedId = (await attachments.StoreStagedAsync(null, null, ms, "old.png", ct)).Id;
                }
                var expiredRelPath = $"attachments/_staged/{expiredStagedId}.png";
                var expiredFullPath = Path.Combine(tempDir, "attachments", "_staged", $"{expiredStagedId}.png");

                string freshStagedId;
                await using (var ms = new MemoryStream(ValidPng))
                {
                    freshStagedId = (await attachments.StoreStagedAsync(null, null, ms, "fresh.png", ct)).Id;
                }

                // Backdate expiredStagedId created_at_ms to > 25h before gcNowMs
                var gcNowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2L * DayMs;
                await using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
                {
                    await conn.OpenAsync(ct);
                    await conn.ExecuteAsync(
                        "UPDATE session_attachments SET created_at_ms = @Ts WHERE id = @Id;",
                        new { Ts = gcNowMs - (25L * 3_600_000L), Id = expiredStagedId });
                    await conn.ExecuteAsync(
                        "UPDATE session_attachments SET created_at_ms = @Ts WHERE id = @Id;",
                        new { Ts = gcNowMs - 60_000L, Id = freshStagedId });
                }

                // Plant an unreferenced orphan file on disk older than 10m grace period
                var orphanDiskPath = Path.Combine(tempDir, "attachments", "_staged", $"att_{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(orphanDiskPath, ValidPng, ct);
                File.SetLastWriteTimeUtc(orphanDiskPath, DateTimeOffset.FromUnixTimeMilliseconds(gcNowMs - 20L * 60_000L).UtcDateTime);

                var stats = await reopened.RunMaintenancePassAsync(gcNowMs, ct);
                Assert.Equal(2, stats.ReclaimedAttachments); // 1 expired staged row + 1 disk orphan
                Assert.False(File.Exists(expiredFullPath));
                Assert.False(File.Exists(orphanDiskPath));
                Assert.Null(await attachments.GetUnownedStagedAsync(expiredStagedId, ct));
                // Fresh staged and bound attachments remain intact
                Assert.NotNull(await attachments.GetUnownedStagedAsync(freshStagedId, ct));
                Assert.NotNull(await attachments.GetForSessionAsync(lead1Id, attNewTrackId, ct));

                // Phase 4: Track deletion removes attachments/{trackId} in O(1) and cascades DB rows
                var trackAttDir = Path.Combine(tempDir, "attachments", track1Id);
                Assert.True(Directory.Exists(trackAttDir));
                await reopened.DeleteTrackAsync(track1Id, ct);
                Assert.False(Directory.Exists(trackAttDir));
                Assert.Null(await attachments.GetForSessionAsync(lead1Id, attNewTrackId, ct));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PinnedPromotion_ResistsConcurrentOrphanGc_PostCommitUnlinkErrors_AndRejectsOutOfRangeLimit()
    {
        // N2: MaxAttachmentsPerMessage > 8 must fail fast at store construction rather than hitting SQLite CHECK later.
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteTrackStore(new SqliteStorageOptions
        {
            DbPath = Path.Combine(Path.GetTempPath(), $"perga-att-invalid-{Guid.NewGuid():N}.db"),
            MaxAttachmentsPerMessage = 9
        }));

        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-att-b123-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "perga.db");
        var stagedDir = Path.Combine(tempDir, "attachments", "_staged");
        try
        {
            await using var store = new SqliteTrackStore(new SqliteStorageOptions { DbPath = dbPath, DataDir = tempDir });
            await store.InitializeAsync();
            var ct = CancellationToken.None;
            var attachments = store.SessionAttachments!;
            await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_gc"), ct);

            // B1: Backdate staged file mtime beyond the 10-minute orphan grace period (still within 24h staged TTL),
            // pre-link & pin into target track dir, run maintenance orphan sweep BEFORE the track creation commits,
            // and verify the pinned destination link is preserved and readable after commit.
            string attRaceId;
            await using (var ms = new MemoryStream(ValidPng))
            {
                attRaceId = (await attachments.StoreStagedAsync(null, null, ms, "race.png", ct)).Id;
            }
            var stagedRacePath = Path.Combine(stagedDir, $"{attRaceId}.png");
            var oldUtc = DateTimeOffset.UtcNow.AddHours(-2);
            File.SetLastWriteTimeUtc(stagedRacePath, oldUtc.UtcDateTime);

            var raceTrackId = "trk_gc_race";
            var raceLeadId = "ses_gc_race";
            var blobStoreField = typeof(SqliteTrackStore).GetField("_context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var context = blobStoreField.GetValue(store)!;
            var repoField = typeof(SqliteTrackStore).GetField("_sessionAttachmentsRepository", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var repo = repoField.GetValue(store)!;
            var prepareMethod = repo.GetType().GetMethod("PrepareStagedLinksForTrackAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var unpinMethod = repo.GetType().GetMethod("UnpinPromotedLinks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var preparedTask = (Task)prepareMethod.Invoke(repo, [raceTrackId, new[] { attRaceId }, true, ct])!;
            await preparedTask;
            var preparedLinks = preparedTask.GetType().GetProperty("Result")!.GetValue(preparedTask)!;
            try
            {
                // Run maintenance orphan sweep while the newly created hardlink (inheriting old mtime) is pinned & not yet in DB!
                var gcStats = await store.RunMaintenancePassAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
                Assert.Equal(0, gcStats.ReclaimedAttachments);

                var promotedPath = Path.Combine(tempDir, "attachments", raceTrackId, $"{attRaceId}.png");
                Assert.True(File.Exists(promotedPath));

                var (raceTrack, raceRep, raceLead) = StorageTestHarness.CreateSeedTrack(raceTrackId, "area_gc", raceLeadId);
                await store.CreateTrackAsync(
                    new TrackCreation(raceTrack, raceRep, null, raceLead, [], null, null, []) { AttachmentIds = [attRaceId] },
                    null, ct);

                var adopted = await attachments.GetForSessionAsync(raceLeadId, attRaceId, ct);
                Assert.NotNull(adopted);
                await using var stream = await attachments.OpenReadStreamAsync(adopted!, ct);
                Assert.NotNull(stream);
                using var copy = new MemoryStream();
                await stream!.CopyToAsync(copy, ct);
                Assert.Equal(ValidPng, copy.ToArray());
            }
            finally
            {
                unpinMethod.Invoke(repo, [preparedLinks]);
            }

            // B1 (atomic check-and-unlink vs pin-and-link race):
            // Plant a pre-existing stale target file (older mtime) at attachments/{trackId}/{attId}.png while the staged
            // source exists in _staged, and race DeleteUnpinnedFileBestEffort against SessionInputs.CreateAsync.
            // Regardless of which side wins _pinGate first, the committed session attachment MUST remain readable!
            var blobStore = context.GetType().GetProperty("AttachmentBlobs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.GetValue(context)!;
            var deleteUnpinnedMethod = blobStore.GetType().GetMethod("DeleteUnpinnedFileBestEffort", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
            for (var iter = 0; iter < 16; iter++)
            {
                string attAtomId;
                await using (var ms = new MemoryStream(ValidPng))
                {
                    attAtomId = (await attachments.StoreStagedAsync(null, null, ms, $"atom-{iter}.png", ct)).Id;
                }

                var targetDir = Path.Combine(tempDir, "attachments", raceTrackId);
                Directory.CreateDirectory(targetDir);
                var staleTargetPath = Path.Combine(targetDir, $"{attAtomId}.png");
                var targetRelPath = $"attachments/{raceTrackId}/{attAtomId}.png";
                File.CreateHardLink(staleTargetPath, Path.Combine(stagedDir, $"{attAtomId}.png"));
                File.SetLastWriteTimeUtc(staleTargetPath, oldUtc.UtcDateTime);

                using var startGate = new ManualResetEventSlim(false);
                var gcTask = Task.Run(() =>
                {
                    startGate.Wait(ct);
                    return (bool)deleteUnpinnedMethod.Invoke(blobStore, [targetRelPath])!;
                }, ct);

                var sesId = $"ses_atom_{iter}";
                var draftSes = new AgentSessionDto(
                    sesId, raceTrackId, false, $"Atom {iter}", "codex", null, null, 1, "queued", null, 300 + iter, 300 + iter);
                var evt = new EventEnvelopeDto(null, "area_gc", raceTrackId, "session.updated", StorageTestHarness.Json("{}"), 300 + iter);
                var createTask = Task.Run(async () =>
                {
                    startGate.Wait(ct);
                    return await store.SessionInputs.CreateAsync(
                        new SessionCreateIntent(raceTrackId, $"k-atom-{iter}", $"h-atom-{iter}"),
                        draftSes,
                        new SessionInputRequest($"k-atom-{iter}", $"h-atom-in-{iter}", "", "owner:installation", [attAtomId]),
                        evt,
                        ct);
                }, ct);

                startGate.Set();
                await Task.WhenAll(gcTask, createTask);

                var boundAtt = await attachments.GetForSessionAsync(sesId, attAtomId, ct);
                Assert.NotNull(boundAtt);
                await using var readStream = await attachments.OpenReadStreamAsync(boundAtt!, ct);
                Assert.NotNull(readStream);
                using var readCopy = new MemoryStream();
                await readStream!.CopyToAsync(readCopy, ct);
                Assert.Equal(ValidPng, readCopy.ToArray());
            }

            // B1 (directory-pruning race on empty track directory using real RunMaintenancePassAsync + SessionInputs.CreateAsync):
            // Create a fresh track whose attachments/{trackId} directory contains ONLY a single stale unreferenced orphan file
            // (so reclaiming it prunes the empty parent directory), and verify BOTH:
            // (a) pinned directory is never pruned while a promotion holds a pin in that directory, and
            // (b) racing RunMaintenancePassAsync against first-attempt SessionInputs.CreateAsync across fresh tracks
            //     always succeeds on the first attempt without DirectoryNotFoundException and preserves readable bytes.
            for (var iter = 0; iter < 12; iter++)
            {
                var pruneTrackId = $"trk_gc_prune_{iter}";
                var pruneLeadId = $"ses_gc_prune_lead_{iter}";
                var (pruneTrack, pruneRep, pruneLead) = StorageTestHarness.CreateSeedTrack(pruneTrackId, "area_gc", pruneLeadId);
                await store.CreateTrackAsync(
                    new TrackCreation(pruneTrack, pruneRep, null, pruneLead, [], null, null, []),
                    null, ct);

                string attPruneId;
                await using (var ms = new MemoryStream(ValidPng))
                {
                    attPruneId = (await attachments.StoreStagedAsync(null, null, ms, $"prune-{iter}.png", ct)).Id;
                }

                // Plant ONLY one unreferenced stale file in attachments/{pruneTrackId}/ so GC prunes the directory when it wins.
                var pruneTrackDir = Path.Combine(tempDir, "attachments", pruneTrackId);
                Directory.CreateDirectory(pruneTrackDir);
                var loneOrphanPath = Path.Combine(pruneTrackDir, $"{attPruneId}.png");
                File.CreateHardLink(loneOrphanPath, Path.Combine(stagedDir, $"{attPruneId}.png"));
                File.SetLastWriteTimeUtc(loneOrphanPath, oldUtc.UtcDateTime);

                if (iter == 0)
                {
                    // Deterministic ordering: run real maintenance reclaim FIRST so the lone orphan is deleted and
                    // attachments/{pruneTrackId} is actually pruned, then assert first-attempt CreateAsync still succeeds.
                    var sweepStats = await store.RunMaintenancePassAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
                    Assert.True(sweepStats.ReclaimedAttachments >= 1);
                    Assert.False(Directory.Exists(pruneTrackDir));

                    var firstSesId = $"ses_prune_seq_{iter}";
                    var firstSes = new AgentSessionDto(
                        firstSesId, pruneTrackId, false, "Prune Seq", "codex", null, null, 1, "queued", null, 400, 400);
                    var firstEvt = new EventEnvelopeDto(null, "area_gc", pruneTrackId, "session.updated", StorageTestHarness.Json("{}"), 400);
                    var seqReceipt = await store.SessionInputs.CreateAsync(
                        new SessionCreateIntent(pruneTrackId, "k-prune-seq", "h-prune-seq"),
                        firstSes,
                        new SessionInputRequest("k-prune-seq", "h-prune-seq-in", "", "owner:installation", [attPruneId]),
                        firstEvt,
                        ct);
                    Assert.NotNull(seqReceipt);
                    Assert.Equal(firstSesId, seqReceipt!.Receipt.SessionId);
                    var seqBound = await attachments.GetForSessionAsync(firstSesId, attPruneId, ct);
                    Assert.NotNull(seqBound);
                    await using var seqStream = await attachments.OpenReadStreamAsync(seqBound!, ct);
                    Assert.NotNull(seqStream);
                    using var seqCopy = new MemoryStream();
                    await seqStream!.CopyToAsync(seqCopy, ct);
                    Assert.Equal(ValidPng, seqCopy.ToArray());

                    // Also verify pinned-directory protection: when another attachment in a track directory is pinned
                    // (e.g. during cross-device fallback), reclaiming the last unpinned orphan file must NOT prune the directory.
                    var pinDirTrackId = "trk_gc_prune_pinned_dir";
                    var pinDir = Path.Combine(tempDir, "attachments", pinDirTrackId);
                    Directory.CreateDirectory(pinDir);
                    var orphanInPinnedDir = Path.Combine(pinDir, "att_00000000000000000000000000000099.png");
                    await File.WriteAllBytesAsync(orphanInPinnedDir, ValidPng, ct);
                    File.SetLastWriteTimeUtc(orphanInPinnedDir, oldUtc.UtcDateTime);
                    var pinMethod = blobStore.GetType().GetMethod("PinRelativePath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
                    var unpinBlobMethod = blobStore.GetType().GetMethod("UnpinRelativePath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!;
                    var pinnedRel = $"attachments/{pinDirTrackId}/att_000000000000000000000000000000aa.png";
                    pinMethod.Invoke(blobStore, [pinnedRel]);
                    try
                    {
                        var pinSweep = await store.RunMaintenancePassAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
                        Assert.True(pinSweep.ReclaimedAttachments >= 1);
                        Assert.False(File.Exists(orphanInPinnedDir));
                        Assert.True(Directory.Exists(pinDir));
                    }
                    finally
                    {
                        unpinBlobMethod.Invoke(blobStore, [pinnedRel]);
                    }
                    continue;
                }

                using var pruneGate = new ManualResetEventSlim(false);
                var maintTask = Task.Run(async () =>
                {
                    pruneGate.Wait(ct);
                    return await store.RunMaintenancePassAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
                }, ct);

                var raceSesId = $"ses_prune_race_{iter}";
                var raceSes = new AgentSessionDto(
                    raceSesId, pruneTrackId, false, $"Prune Race {iter}", "codex", null, null, 1, "queued", null, 410 + iter, 410 + iter);
                var raceEvt = new EventEnvelopeDto(null, "area_gc", pruneTrackId, "session.updated", StorageTestHarness.Json("{}"), 410 + iter);
                var raceCreateTask = Task.Run(async () =>
                {
                    pruneGate.Wait(ct);
                    return await store.SessionInputs.CreateAsync(
                        new SessionCreateIntent(pruneTrackId, $"k-prune-race-{iter}", $"h-prune-race-{iter}"),
                        raceSes,
                        new SessionInputRequest($"k-prune-race-{iter}", $"h-prune-race-in-{iter}", "", "owner:installation", [attPruneId]),
                        raceEvt,
                        ct);
                }, ct);

                pruneGate.Set();
                await Task.WhenAll(maintTask, raceCreateTask);
                var raceReceipt = await raceCreateTask;
                Assert.NotNull(raceReceipt);
                Assert.Equal(raceSesId, raceReceipt!.Receipt.SessionId);

                var raceBound = await attachments.GetForSessionAsync(raceSesId, attPruneId, ct);
                Assert.NotNull(raceBound);
                await using var raceStream = await attachments.OpenReadStreamAsync(raceBound!, ct);
                Assert.NotNull(raceStream);
                using var raceCopy = new MemoryStream();
                await raceStream!.CopyToAsync(raceCopy, ct);
                Assert.Equal(ValidPng, raceCopy.ToArray());
            }

            // B2: Make _staged readable/linkable but non-unlinkable (0500 on Linux) so post-commit old-file cleanup hits
            // UnauthorizedAccessException. Commit must still succeed and preserve the committed destination file!
            if (!OperatingSystem.IsWindows())
            {
                string attNoUnlinkTrackId;
                await using (var ms = new MemoryStream(ValidPng))
                {
                    attNoUnlinkTrackId = (await attachments.StoreStagedAsync(null, null, ms, "nounlink-track.png", ct)).Id;
                }
                string attNoUnlinkSessionId;
                await using (var ms = new MemoryStream(ValidWebP))
                {
                    attNoUnlinkSessionId = (await attachments.StoreStagedAsync(null, null, ms, "nounlink-session.webp", ct)).Id;
                }

                File.SetUnixFileMode(stagedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    var (t2, r2, l2) = StorageTestHarness.CreateSeedTrack("trk_post_commit", "area_gc", "ses_post_commit_lead");
                    await store.CreateTrackAsync(
                        new TrackCreation(t2, r2, null, l2, [], null, null, []) { AttachmentIds = [attNoUnlinkTrackId] },
                        null, ct);
                    var trackAtt = await attachments.GetForSessionAsync("ses_post_commit_lead", attNoUnlinkTrackId, ct);
                    Assert.NotNull(trackAtt);
                    await using (var s = await attachments.OpenReadStreamAsync(trackAtt!, ct))
                    {
                        Assert.NotNull(s);
                        using var buf = new MemoryStream();
                        await s!.CopyToAsync(buf, ct);
                        Assert.Equal(ValidPng, buf.ToArray());
                    }

                    var draftSes = new AgentSessionDto(
                        "ses_post_commit_draft", "trk_post_commit", false, "Draft", "codex", null, null, 1, "queued", null, 100, 100);
                    var evt = new EventEnvelopeDto(null, "area_gc", "trk_post_commit", "session.updated", StorageTestHarness.Json("{}"), 100);
                    var created = await store.SessionInputs.CreateAsync(
                        new SessionCreateIntent("trk_post_commit", "k-post-commit", "h-post-commit"),
                        draftSes,
                        new SessionInputRequest("k-post-commit", "h-post-commit-in", "", "owner:installation", [attNoUnlinkSessionId]),
                        evt,
                        ct);
                    Assert.NotNull(created);
                    var draftAtt = await attachments.GetForSessionAsync("ses_post_commit_draft", attNoUnlinkSessionId, ct);
                    Assert.NotNull(draftAtt);
                    await using (var s = await attachments.OpenReadStreamAsync(draftAtt!, ct))
                    {
                        Assert.NotNull(s);
                        using var buf = new MemoryStream();
                        await s!.CopyToAsync(buf, ct);
                        Assert.Equal(ValidWebP, buf.ToArray());
                    }
                }
                finally
                {
                    File.SetUnixFileMode(stagedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            // B3: SessionMessages.InsertAsync returns the bound Attachments directly inside the insert transaction.
            string attSteerId;
            await using (var ms = new MemoryStream(ValidJpeg))
            {
                attSteerId = (await attachments.StoreStagedAsync("trk_gc_race", "ses_gc_race", ms, "steer.jpg", ct)).Id;
            }
            var insertedSteer = await store.SessionMessages.InsertAsync(
                new SessionMessage("msg_direct_steer", "ses_gc_race", "owner:installation", "k-steer-ret", "h-steer-ret",
                    "trn_1", null, "", "dispatching", 200, "steer")
                {
                    AttachmentIds = [attSteerId]
                },
                ct);
            var boundSteerAtt = Assert.Single(insertedSteer.Attachments!);
            Assert.Equal(attSteerId, boundSteerAtt.Id);
            Assert.Equal("bound", boundSteerAtt.Status);
            Assert.True(File.Exists(boundSteerAtt.PhysicalPath));
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && Directory.Exists(stagedDir))
            {
                try { File.SetUnixFileMode(stagedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            }
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
