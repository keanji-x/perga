using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.Execution;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class SessionAttachmentsIntegrationTests
{
    private static readonly byte[] SamplePng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01
    ];

    private static readonly byte[] SampleJpeg =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46,
        0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01
    ];

    [Fact]
    public async Task UploadDownloadSecurityBoundaries_MagicSniff_Limits_Quotas_IdorAndHeaders()
    {
        await using var host = new PergaHostTestCluster(
            configureHostOptions: opts =>
            {
                opts.MaxAttachmentBytes = 128;
                opts.MaxSessionAttachmentBytes = 160;
            });

        var (trackId, leadId) = await host.CreateTrackAsync();
        using var otherSessionResp = await PostWithKeyAsync(host.Client, $"/api/v1/tracks/{trackId}/sessions", "ses-other", new { title = "Other" });
        Assert.Equal(HttpStatusCode.Created, otherSessionResp.StatusCode);
        var otherSessionId = (await otherSessionResp.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!.Session.Id;

        // 1. Rejects SVG, text/plain, empty body with 400 invalid_argument
        using (var svgResp = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", Encoding.UTF8.GetBytes("<svg></svg>"), "image/svg+xml"))
        {
            await AssertErrorAsync(svgResp, HttpStatusCode.BadRequest, "invalid_argument");
        }
        using (var textResp = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", SamplePng, "text/plain"))
        {
            await AssertErrorAsync(textResp, HttpStatusCode.BadRequest, "invalid_argument");
        }
        using (var emptyResp = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", [], "application/octet-stream"))
        {
            await AssertErrorAsync(emptyResp, HttpStatusCode.BadRequest, "invalid_argument");
        }

        // 2. Rejects > MaxAttachmentBytes with 413 payload_too_large (both with Content-Length and chunked without Content-Length)
        var oversize = SamplePng.Concat(new byte[200]).ToArray();
        using (var bigResp = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", oversize, "application/octet-stream"))
        {
            await AssertErrorAsync(bigResp, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        }
        using (var chunkedContent = new PushStreamContent(oversize))
        using (var bigChunkedResp = await host.Client.PostAsync($"/api/v1/sessions/{leadId}/attachments", chunkedContent))
        {
            await AssertErrorAsync(bigChunkedResp, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        }

        // 3. Valid PNG upload (even with untrusted application/octet-stream header) returns 201 and sniffed image/png
        using var uploadResp = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", SamplePng, "application/octet-stream", "screenshot.png");
        Assert.Equal(HttpStatusCode.Created, uploadResp.StatusCode);
        var uploaded = (await uploadResp.Content.ReadFromJsonAsync<UploadAttachmentResponseDto>(PergaJsonDefaults.ApiOptions))!;
        Assert.StartsWith("att_", uploaded.AttachmentId, StringComparison.Ordinal);
        Assert.Equal("image/png", uploaded.ContentType);
        Assert.Equal(SamplePng.Length, uploaded.Size);
        Assert.Equal($"/api/v1/sessions/{leadId}/attachments/{uploaded.AttachmentId}", uploaded.Url);

        // 4. Download verifies mandatory security headers, ETag, 304, and staged Cache-Control
        string etag;
        using (var getResp = await host.Client.GetAsync(uploaded.Url))
        {
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            Assert.Equal("image/png", getResp.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", Assert.Single(getResp.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("sandbox", Assert.Single(getResp.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("inline", Assert.Single(getResp.Content.Headers.GetValues("Content-Disposition")));
            Assert.True(getResp.Headers.CacheControl?.Private);
            Assert.True(getResp.Headers.CacheControl?.NoStore);
            etag = Assert.Single(getResp.Headers.GetValues("ETag"));
            Assert.Equal(SamplePng, await getResp.Content.ReadAsByteArrayAsync());
        }

        using (var conditionalReq = new HttpRequestMessage(HttpMethod.Get, uploaded.Url))
        {
            conditionalReq.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var notModResp = await host.Client.SendAsync(conditionalReq);
            Assert.Equal(HttpStatusCode.NotModified, notModResp.StatusCode);
            Assert.Equal("nosniff", Assert.Single(notModResp.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("sandbox", Assert.Single(notModResp.Headers.GetValues("Content-Security-Policy")));
        }

        // 5. Cross-session IDOR and invalid IDs uniformly return 404
        using (var idorResp = await host.Client.GetAsync($"/api/v1/sessions/{otherSessionId}/attachments/{uploaded.AttachmentId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, idorResp.StatusCode);
        }
        using (var unownedBypassResp = await host.Client.GetAsync($"/api/v1/attachments/{uploaded.AttachmentId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unownedBypassResp.StatusCode);
        }
        using (var invalidIdResp = await host.Client.GetAsync($"/api/v1/sessions/{leadId}/attachments/not-valid"))
        {
            Assert.Equal(HttpStatusCode.NotFound, invalidIdResp.StatusCode);
        }

        // 6. Session quota exceeded -> 400 attachment_quota_exceeded
        var mediumPng = SamplePng.Concat(new byte[80]).ToArray(); // 104 bytes
        using (var okMedium = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", mediumPng, "image/png"))
        {
            Assert.Equal(HttpStatusCode.Created, okMedium.StatusCode);
        }
        using (var overQuota = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", mediumPng, "image/png"))
        {
            await AssertErrorAsync(overQuota, HttpStatusCode.BadRequest, "attachment_quota_exceeded");
        }

        // 7. Reject > 8 attachments and duplicate attachment IDs on send
        var nineIds = Enumerable.Range(0, 9).Select(_ => $"att_{Guid.NewGuid():N}").ToArray();
        using (var tooManyResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-9", new { attachmentIds = nineIds }))
        {
            await AssertErrorAsync(tooManyResp, HttpStatusCode.BadRequest, "invalid_argument");
        }
        using (var dupResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-dup", new { attachmentIds = new[] { uploaded.AttachmentId, uploaded.AttachmentId } }))
        {
            await AssertErrorAsync(dupResp, HttpStatusCode.BadRequest, "invalid_argument");
        }
    }

    [Fact]
    public async Task ImageOnlySend_IdempotencyReplayAndConflict_BoundReuse_AndCapabilitiesGuard()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, leadId) = await host.CreateTrackAsync();

        // Upload PNG to lead session
        using var upResp = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", SamplePng, "image/png");
        Assert.Equal(HttpStatusCode.Created, upResp.StatusCode);
        var att = (await upResp.Content.ReadFromJsonAsync<UploadAttachmentResponseDto>(PergaJsonDefaults.ApiOptions))!;

        // Image-only send without Idempotency-Key -> 400
        using (var noKeyResp = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{leadId}/messages", new { attachmentIds = new[] { att.AttachmentId } }))
        {
            await AssertErrorAsync(noKeyResp, HttpStatusCode.BadRequest, "invalid_argument");
        }

        // Image-only send succeeds
        using var sendResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "img-only-1", new { attachmentIds = new[] { att.AttachmentId } });
        Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);
        var receipt = (await sendResp.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.False(receipt.Replay);

        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);

        // Runtime received empty prompt + InputImageRef with valid physical file and mediaType
        var turn = Assert.Single(host.AgentRuntime.ExecutedTurns);
        Assert.Equal(string.Empty, turn.Input.Prompt);
        var deliveredImg = Assert.Single(turn.Input.Images!);
        Assert.Equal("image/png", deliveredImg.MediaType);
        Assert.EndsWith(".png", deliveredImg.Path, StringComparison.Ordinal);
        Assert.True(File.Exists(deliveredImg.Path));

        // Bound attachment now serves immutable Cache-Control
        using (var boundGet = await host.Client.GetAsync(att.Url))
        {
            Assert.Equal(HttpStatusCode.OK, boundGet.StatusCode);
            Assert.Contains("immutable", boundGet.Headers.CacheControl?.ToString() ?? string.Empty);
        }

        // Replay with identical Idempotency-Key & attachmentIds returns replay=true without duplicate turn
        using (var replayResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "img-only-1", new { attachmentIds = new[] { att.AttachmentId } }))
        {
            Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);
            var replay = (await replayResp.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
            Assert.True(replay.Replay);
            Assert.Equal(receipt.MessageId, replay.MessageId);
        }
        Assert.Single(host.AgentRuntime.ExecutedTurns);

        // Reusing Idempotency-Key with DIFFERENT attachmentIds -> 409 conflict
        using var upResp2 = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", SampleJpeg, "image/jpeg");
        var att2 = (await upResp2.Content.ReadFromJsonAsync<UploadAttachmentResponseDto>(PergaJsonDefaults.ApiOptions))!;
        using (var conflictResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "img-only-1", new { attachmentIds = new[] { att2.AttachmentId } }))
        {
            await AssertErrorAsync(conflictResp, HttpStatusCode.Conflict, "conflict");
        }

        // Re-send / regenerate reuses already-bound attachment ID in the same session!
        using (var reuseResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "img-reuse-2", new { content = "try again", attachmentIds = new[] { att.AttachmentId, att2.AttachmentId } }))
        {
            Assert.Equal(HttpStatusCode.OK, reuseResp.StatusCode);
        }
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
        Assert.Equal(2, host.AgentRuntime.ExecutedTurns.Count);
        var secondTurn = host.AgentRuntime.ExecutedTurns.Last();
        Assert.Equal(2, secondTurn.Input.Images!.Count);
        Assert.Equal(["image/png", "image/jpeg"], secondTurn.Input.Images.Select(i => i.MediaType));

        // Items endpoint hydrates attachments and reports capabilities.supportsImages = true
        var page = (await host.Client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{leadId}/items", PergaJsonDefaults.ApiOptions))!;
        Assert.True(page.Capabilities?.SupportsImages);
        var userItems = page.Items.Where(i => i.Kind == "user").ToList();
        Assert.Equal(2, userItems.Count);
        Assert.Equal([att.AttachmentId], userItems[0].Attachments!.Select(a => a.Id));
        Assert.Equal([att.AttachmentId, att2.AttachmentId], userItems[1].Attachments!.Select(a => a.Id));

        // Deleting the Track removes the Track's attachment directory on disk (O(1) cleanup)
        using (var delResp = await host.Client.DeleteAsync($"/api/v1/tracks/{trackId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);
        }
        Assert.False(File.Exists(deliveredImg.Path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(deliveredImg.Path)!));
    }

    [Fact]
    public async Task UnsupportedRuntime_ReportsCapabilityFalse_AndRefusesImageSendWithoutSilentlyDropping()
    {
        await using var host = new PergaHostTestCluster();
        host.AgentRuntime.ProviderImageSupport = provider => provider == "claude" ? false : null;
        host.AgentRuntime.SessionImageSupport = _ => false;
        host.AgentRuntime.ProviderStatuses =
        [
            new("codex", Available: true, Version: "1.0", Reason: null, Models: ["gpt-5.4"], SupportsImages: null),
            new("claude", Available: true, Version: "1.0", Reason: null, Models: ["claude-sonnet-4-6"], SupportsImages: false),
        ];

        var (trackId, leadId) = await host.CreateTrackAsync();

        // 1. Statically unsupported provider (claude): unopened session reports supportsImages=false and rejects send with 409 precondition_failed
        using var claudeSesResp = await PostWithKeyAsync(host.Client, $"/api/v1/tracks/{trackId}/sessions", "ses-claude", new { provider = "claude" });
        Assert.Equal(HttpStatusCode.Created, claudeSesResp.StatusCode);
        var claudeSid = (await claudeSesResp.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!.Session.Id;

        var claudePage = (await host.Client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{claudeSid}/items", PergaJsonDefaults.ApiOptions))!;
        Assert.False(claudePage.Capabilities!.SupportsImages);

        using var upClaude = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{claudeSid}/attachments", SamplePng, "image/png");
        var claudeAtt = (await upClaude.Content.ReadFromJsonAsync<UploadAttachmentResponseDto>(PergaJsonDefaults.ApiOptions))!;

        using (var refusedResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{claudeSid}/messages", "k-refuse", new { content = "img", attachmentIds = new[] { claudeAtt.AttachmentId } }))
        {
            await AssertErrorAsync(refusedResp, HttpStatusCode.Conflict, "precondition_failed");
        }
        Assert.Empty(host.AgentRuntime.ExecutedTurns);

        // 2. Handshake-determined provider (codex with null pre-open, false post-open):
        //    admits turn while unopened, opens session, observes SupportsImages==false, and fails conclusively BeforePrompt without calling StreamTurnAsync!
        var leadPageBefore = (await host.Client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{leadId}/items", PergaJsonDefaults.ApiOptions))!;
        Assert.True(leadPageBefore.Capabilities!.SupportsImages); // unknown pre-open -> true

        using var upLead = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", SamplePng, "image/png");
        var leadAtt = (await upLead.Content.ReadFromJsonAsync<UploadAttachmentResponseDto>(PergaJsonDefaults.ApiOptions))!;

        using (var sentResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-handshake", new { attachmentIds = new[] { leadAtt.AttachmentId } }))
        {
            Assert.Equal(HttpStatusCode.OK, sentResp.StatusCode);
        }
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);

        // Never invoked StreamTurnAsync without images!
        Assert.Empty(host.AgentRuntime.ExecutedTurns);
        var leadPageAfter = (await host.Client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{leadId}/items", PergaJsonDefaults.ApiOptions))!;
        Assert.False(leadPageAfter.Capabilities!.SupportsImages); // authoritative post-open!
        Assert.Equal("failed", leadPageAfter.LastTurn?.Status);
        Assert.Contains("does not support image", leadPageAfter.LastTurn?.ErrorText ?? string.Empty);
    }

    [Fact]
    public async Task NewTrackAndNewSessionDraftFlows_AdoptPromoteDeliverAndSurviveHostRestart()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"perga-att-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            string leadSessionId;
            string leadAttachmentId;
            string draftSessionId;
            string draftAttachmentId;

            // Host instance #1
            await using (var host1 = new PergaHostTestCluster(explicitDataDir: dataDir))
            {
                var client1 = host1.Client;
                var kernel1 = host1.Services.GetRequiredService<IPergaKernel>();

                // Create area
                using var areaResp = await client1.PostAsJsonAsync("/api/v1/areas", new { name = "Attachments Area", defaultCwd = dataDir });
                Assert.Equal(HttpStatusCode.Created, areaResp.StatusCode);
                var areaId = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions))!.Id;

                // Flow 1: New Track Page (upload unowned staged -> create track with attachmentIds -> initial image-only send)
                using var stagedTrackUp = await UploadBinaryAsync(client1, "/api/v1/attachments", SamplePng, "image/png", "new-track.png");
                Assert.Equal(HttpStatusCode.Created, stagedTrackUp.StatusCode);
                var stagedTrackAtt = (await stagedTrackUp.Content.ReadFromJsonAsync<UploadAttachmentResponseDto>(PergaJsonDefaults.ApiOptions))!;
                leadAttachmentId = stagedTrackAtt.AttachmentId;
                Assert.Equal($"/api/v1/attachments/{leadAttachmentId}", stagedTrackAtt.Url);

                // Verify readable while unowned staged
                using (var getStaged = await client1.GetAsync(stagedTrackAtt.Url))
                {
                    Assert.Equal(HttpStatusCode.OK, getStaged.StatusCode);
                }

                // Create Track with Idempotency-Key + attachmentIds
                using var trackResp = await PostWithKeyAsync(client1, "/api/v1/tracks", "trk-create-img", new
                {
                    areaId,
                    cwd = dataDir,
                    title = "Image Track",
                    attachmentIds = new[] { leadAttachmentId }
                });
                Assert.Equal(HttpStatusCode.Created, trackResp.StatusCode);
                var snap = (await trackResp.Content.ReadFromJsonAsync<TrackAggregateSnapshot>(PergaJsonDefaults.ApiOptions))!;
                var trackId = snap.Track.Id;
                leadSessionId = snap.Track.LeadSessionId!;

                // Idempotent CreateTrack replay succeeds; conflict with different attachmentIds returns 409
                using (var replayTrack = await PostWithKeyAsync(client1, "/api/v1/tracks", "trk-create-img", new
                {
                    areaId,
                    cwd = dataDir,
                    title = "Image Track",
                    attachmentIds = new[] { leadAttachmentId }
                }))
                {
                    Assert.Equal(HttpStatusCode.Created, replayTrack.StatusCode);
                    var replaySnap = (await replayTrack.Content.ReadFromJsonAsync<TrackAggregateSnapshot>(PergaJsonDefaults.ApiOptions))!;
                    Assert.Equal(trackId, replaySnap.Track.Id);
                }
                using (var conflictTrack = await PostWithKeyAsync(client1, "/api/v1/tracks", "trk-create-img", new
                {
                    areaId,
                    cwd = dataDir,
                    title = "Image Track",
                    attachmentIds = new[] { $"att_{Guid.NewGuid():N}" }
                }))
                {
                    await AssertErrorAsync(conflictTrack, HttpStatusCode.Conflict, "idempotency_key_reused");
                }

                // Adopted attachment is now session-scoped (unowned GET returns 404; session GET returns 200)
                using (var oldUrlResp = await client1.GetAsync(stagedTrackAtt.Url))
                {
                    Assert.Equal(HttpStatusCode.NotFound, oldUrlResp.StatusCode);
                }
                using (var newUrlResp = await client1.GetAsync($"/api/v1/sessions/{leadSessionId}/attachments/{leadAttachmentId}"))
                {
                    Assert.Equal(HttpStatusCode.OK, newUrlResp.StatusCode);
                }

                // Send initial image-only message on lead session
                using (var leadSend = await PostWithKeyAsync(client1, $"/api/v1/sessions/{leadSessionId}/messages", "lead-first", new
                {
                    attachmentIds = new[] { leadAttachmentId }
                }))
                {
                    Assert.Equal(HttpStatusCode.OK, leadSend.StatusCode);
                }

                // Flow 2: New Session Draft (upload ?trackId={trackId} -> POST /tracks/{trackId}/sessions with image-only first input)
                using var stagedDraftUp = await UploadBinaryAsync(client1, $"/api/v1/attachments?trackId={trackId}", SampleJpeg, "image/jpeg", "draft.jpg");
                Assert.Equal(HttpStatusCode.Created, stagedDraftUp.StatusCode);
                var stagedDraftAtt = (await stagedDraftUp.Content.ReadFromJsonAsync<UploadAttachmentResponseDto>(PergaJsonDefaults.ApiOptions))!;
                draftAttachmentId = stagedDraftAtt.AttachmentId;

                using var draftCreateResp = await PostWithKeyAsync(client1, $"/api/v1/tracks/{trackId}/sessions", "draft-first", new
                {
                    title = "Draft With Image",
                    prompt = (string?)null,
                    attachmentIds = new[] { draftAttachmentId }
                });
                Assert.Equal(HttpStatusCode.Created, draftCreateResp.StatusCode);
                var draftReceipt = (await draftCreateResp.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
                draftSessionId = draftReceipt.Session.Id;
                Assert.NotNull(draftReceipt.FirstInput);

                // Bound draft attachment is no longer readable on /api/v1/attachments/{id}
                using (var boundStagedResp = await client1.GetAsync(stagedDraftAtt.Url))
                {
                    Assert.Equal(HttpStatusCode.NotFound, boundStagedResp.StatusCode);
                }

                await kernel1.WaitForTrackIdleAsync(trackId);
                Assert.Equal(2, host1.AgentRuntime.ExecutedTurns.Count);

                // N3: Assert actual TurnInput.Images delivered to provider on both New Track Lead and New Session Draft turns.
                var leadTurn = Assert.Single(host1.AgentRuntime.ExecutedTurns, t => t.Input.Images?.Any(img => img.Path.Contains(leadAttachmentId, StringComparison.Ordinal)) == true);
                var leadTurnImg = Assert.Single(leadTurn.Input.Images!);
                Assert.Equal("image/png", leadTurnImg.MediaType);
                Assert.True(File.Exists(leadTurnImg.Path));
                Assert.Equal(SamplePng, await File.ReadAllBytesAsync(leadTurnImg.Path));

                var draftTurn = Assert.Single(host1.AgentRuntime.ExecutedTurns, t => t.Input.Images?.Any(img => img.Path.Contains(draftAttachmentId, StringComparison.Ordinal)) == true);
                var draftTurnImg = Assert.Single(draftTurn.Input.Images!);
                Assert.Equal("image/jpeg", draftTurnImg.MediaType);
                Assert.True(File.Exists(draftTurnImg.Path));
                Assert.Equal(SampleJpeg, await File.ReadAllBytesAsync(draftTurnImg.Path));
            }

            // Host instance #2 (reopen same dataDir from scratch to verify restart durability!)
            await using (var host2 = new PergaHostTestCluster(explicitDataDir: dataDir))
            {
                var client2 = host2.Client;

                var leadPage = (await client2.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{leadSessionId}/items", PergaJsonDefaults.ApiOptions))!;
                var leadUser = Assert.Single(leadPage.Items, i => i.Kind == "user");
                var leadHydrated = Assert.Single(leadUser.Attachments!);
                Assert.Equal(leadAttachmentId, leadHydrated.Id);
                Assert.Equal("image/png", leadHydrated.ContentType);

                using (var getLead = await client2.GetAsync(leadHydrated.Url))
                {
                    Assert.Equal(HttpStatusCode.OK, getLead.StatusCode);
                    Assert.Equal(SamplePng, await getLead.Content.ReadAsByteArrayAsync());
                }

                var draftPage = (await client2.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{draftSessionId}/items", PergaJsonDefaults.ApiOptions))!;
                var draftUser = Assert.Single(draftPage.Items, i => i.Kind == "user");
                var draftHydrated = Assert.Single(draftUser.Attachments!);
                Assert.Equal(draftAttachmentId, draftHydrated.Id);
                Assert.Equal("image/jpeg", draftHydrated.ContentType);

                using (var getDraft = await client2.GetAsync(draftHydrated.Url))
                {
                    Assert.Equal(HttpStatusCode.OK, getDraft.StatusCode);
                    Assert.Equal(SampleJpeg, await getDraft.Content.ReadAsByteArrayAsync());
                }
            }
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SshExecutionFileSystem_StagesIdempotently_RepairsCorruption_CleansUp_AndFailsClosed()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), $"perga-ssh-att-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sshWrapper = Path.Combine(root, "fake-ssh.sh");
            var callLog = Path.Combine(root, "ssh-calls.log");
            await File.WriteAllTextAsync(sshWrapper, $$"""
                #!/bin/sh
                echo "call" >> '{{callLog}}'
                for arg do last=$arg; done
                exec sh -c "$last"
                """);
            File.SetUnixFileMode(sshWrapper,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var remoteWorkspaces = Path.Combine(root, "remote-ws");
            Directory.CreateDirectory(remoteWorkspaces);

            var config = new SshTargetConfig("remote", new ExecutionTargetOptions
            {
                Host = "127.0.0.1",
                User = "test",
                WorkspacesRoot = remoteWorkspaces,
                SshExecutablePath = sshWrapper
            });
            var fs = new SshExecutionFileSystem(config);

            var localFile = Path.Combine(root, "local.png");
            await File.WriteAllBytesAsync(localFile, SamplePng);
            var sha = Convert.ToHexStringLower(SHA256.HashData(SamplePng));
            var attId = $"att_{Guid.NewGuid():N}";

            // 1. First stage uploads file (check + write = 2 ssh calls)
            var stagedPath = await fs.StageAttachmentAsync("trk_remote", attId, ".png", localFile, sha);
            Assert.True(File.Exists(stagedPath));
            Assert.Equal(SamplePng, await File.ReadAllBytesAsync(stagedPath));
            var callsAfterFirst = (await File.ReadAllLinesAsync(callLog)).Length;
            Assert.Equal(2, callsAfterFirst);

            // 2. Second stage hits fast check path only (idempotent: +1 ssh call, no rewrite)
            var stagedPath2 = await fs.StageAttachmentAsync("trk_remote", attId, ".png", localFile, sha);
            Assert.Equal(stagedPath, stagedPath2);
            var callsAfterSecond = (await File.ReadAllLinesAsync(callLog)).Length;
            Assert.Equal(3, callsAfterSecond);

            // 3. Corrupted remote file is detected by sha256 check and atomically repaired
            await File.WriteAllBytesAsync(stagedPath, SamplePng[..4]);
            var repairedPath = await fs.StageAttachmentAsync("trk_remote", attId, ".png", localFile, sha);
            Assert.Equal(stagedPath, repairedPath);
            Assert.Equal(SamplePng, await File.ReadAllBytesAsync(repairedPath));

            // 4. Invalid inputs rejected before invoking ssh
            await Assert.ThrowsAsync<KernelException>(() => fs.StageAttachmentAsync("../bad", attId, ".png", localFile, sha));
            await Assert.ThrowsAsync<KernelException>(() => fs.StageAttachmentAsync("trk_remote", "bad-id", ".png", localFile, sha));
            await Assert.ThrowsAsync<KernelException>(() => fs.StageAttachmentAsync("trk_remote", attId, ".svg", localFile, sha));

            // 5. Symlinked track directory must be rejected even on fast cache-hit check (N1)
            var outsideDir = Path.Combine(root, "outside");
            Directory.CreateDirectory(outsideDir);
            await File.WriteAllBytesAsync(Path.Combine(outsideDir, $"{attId}.png"), SamplePng);
            var symlinkTrackDir = Path.Combine(remoteWorkspaces, ".perga-attachments", "trk_symlink");
            Directory.CreateSymbolicLink(symlinkTrackDir, outsideDir);
            var symEx = await Assert.ThrowsAsync<KernelException>(() => fs.StageAttachmentAsync("trk_symlink", attId, ".png", localFile, sha));
            Assert.Equal(KernelErrorCode.Forbidden, symEx.Code);

            // 6. CleanupTrackAttachmentsAsync removes track directory on remote target
            await fs.CleanupTrackAttachmentsAsync("trk_remote");
            Assert.False(Directory.Exists(Path.Combine(remoteWorkspaces, ".perga-attachments", "trk_remote")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task QueuedMessageWithImages_HydratesQueueAndDeliversOnPromotion_AndStagingFailureFailsClosedBeforePrompt()
    {
        var releaseFirstTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTurnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failStaging = false;

        await using var host = new PergaHostTestCluster(
            configureExtraServices: services =>
            {
                services.RemoveAll<IExecutionEnvironmentRegistry>();
                services.AddSingleton<IExecutionEnvironmentRegistry>(sp =>
                {
                    var paths = sp.GetRequiredService<ResolvedHostStoragePaths>();
                    var local = new LocalExecutionEnvironment(
                        workspacesRoot: paths.WorkspacesRootDir,
                        agentRuntime: sp.GetRequiredService<IAgentRuntime>(),
                        shell: sp.GetService<IShellCommandRunner>() ?? new ProcessShellCommandRunner(),
                        vcs: sp.GetRequiredService<Perga.VcsKit.IVcsService>(),
                        workspaceDrivers: sp.GetRequiredService<Perga.VcsKit.IWorkspaceDriverRegistry>(),
                        terminals: sp.GetRequiredService<Perga.TermClient.ITerminalBridgeClient>(),
                        files: new ToggleableStageFileSystem(() => failStaging));
                    return new ExecutionEnvironmentRegistry([local]);
                });
            });

        host.AgentRuntime.SetTurnHandler((spec, input, ct) =>
        {
            async IAsyncEnumerable<UnifiedAgentEvent> Stream()
            {
                if (input.Prompt == "hold-first")
                {
                    firstTurnStarted.TrySetResult();
                    await releaseFirstTurn.Task.WaitAsync(ct);
                }
                yield return new UnifiedAgentEvent.TextDelta($"ok:{input.Prompt}|imgs:{input.Images?.Count ?? 0}");
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(10, 5, 0, 100_000));
            }

            return Stream();
        });

        var (trackId, leadId) = await host.CreateTrackAsync();

        // Start turn 1 and hold it running
        using var firstResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-hold", new { content = "hold-first" });
        Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);
        var firstReceipt = (await firstResp.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        await firstTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Upload PNG and steer an image-only message into the active turn, then queue another image-only follow-up
        using var upResp = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", SamplePng, "image/png");
        Assert.Equal(HttpStatusCode.Created, upResp.StatusCode);
        var att = (await upResp.Content.ReadFromJsonAsync<UploadAttachmentResponseDto>(PergaJsonDefaults.ApiOptions))!;

        using (var steerResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-steer-img", new
        {
            mode = "steer",
            turnId = firstReceipt.TurnId,
            attachmentIds = new[] { att.AttachmentId }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, steerResp.StatusCode);
        }
        var steered = Assert.Single(host.AgentRuntime.SteeredMessages);
        Assert.Equal(string.Empty, steered.Message);
        Assert.Single(steered.Images!);
        Assert.Equal("image/png", steered.Images![0].MediaType);

        using var qResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-queued-img", new
        {
            mode = "queue",
            attachmentIds = new[] { att.AttachmentId }
        });
        Assert.Equal(HttpStatusCode.OK, qResp.StatusCode);

        // Queue readback includes hydrated attachments!
        var midPage = (await host.Client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{leadId}/items", PergaJsonDefaults.ApiOptions))!;
        var queued = Assert.Single(midPage.QueuedMessages);
        Assert.Equal([att.AttachmentId], queued.Attachments!.Select(a => a.Id));

        // Release turn 1 -> queued image-only message promotes and executes with image!
        releaseFirstTurn.TrySetResult();
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);

        Assert.Equal(2, host.AgentRuntime.ExecutedTurns.Count);
        var promotedTurn = host.AgentRuntime.ExecutedTurns.Last();
        Assert.Equal(string.Empty, promotedTurn.Input.Prompt);
        Assert.Single(promotedTurn.Input.Images!);

        // Now simulate remote staging failure -> turn must fail conclusively BeforePrompt without calling StreamTurnAsync!
        failStaging = true;
        using var failSend = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-stage-fail", new
        {
            content = "must not send without image",
            attachmentIds = new[] { att.AttachmentId }
        });
        Assert.Equal(HttpStatusCode.OK, failSend.StatusCode);
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);

        Assert.Equal(2, host.AgentRuntime.ExecutedTurns.Count); // NEVER dispatched turn 3!
        var afterFail = (await host.Client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{leadId}/items", PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("failed", afterFail.LastTurn?.Status);
        Assert.Contains("attachment_stage_failed", afterFail.LastTurn?.ErrorText ?? string.Empty);
    }

    [Fact]
    public async Task SteeringStagingTimeout_ReturnsQueuedOrRejected_NeverUnknown_AndNeverCallsSteer()
    {
        var firstTurnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stageShouldHang = true;

        await using var host = new PergaHostTestCluster(
            configureExtraServices: services =>
            {
                services.RemoveAll<IExecutionEnvironmentRegistry>();
                services.AddSingleton<IExecutionEnvironmentRegistry>(sp =>
                {
                    var paths = sp.GetRequiredService<ResolvedHostStoragePaths>();
                    var local = new LocalExecutionEnvironment(
                        workspacesRoot: paths.WorkspacesRootDir,
                        agentRuntime: sp.GetRequiredService<IAgentRuntime>(),
                        shell: sp.GetService<IShellCommandRunner>() ?? new ProcessShellCommandRunner(),
                        vcs: sp.GetRequiredService<Perga.VcsKit.IVcsService>(),
                        workspaceDrivers: sp.GetRequiredService<Perga.VcsKit.IWorkspaceDriverRegistry>(),
                        terminals: sp.GetRequiredService<Perga.TermClient.ITerminalBridgeClient>(),
                        files: new DelayableStageFileSystem(() => stageShouldHang));
                    return new ExecutionEnvironmentRegistry([local]);
                });
            });

        host.AgentRuntime.SetTurnHandler((spec, input, ct) =>
        {
            async IAsyncEnumerable<UnifiedAgentEvent> Stream()
            {
                if (input.Prompt == "hold-timeout")
                {
                    firstTurnStarted.TrySetResult();
                    await releaseFirstTurn.Task.WaitAsync(ct);
                }
                yield return new UnifiedAgentEvent.TextDelta($"ok:{input.Prompt}|imgs:{input.Images?.Count ?? 0}");
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(10, 5, 0, 100_000));
            }

            return Stream();
        });

        var (trackId, leadId) = await host.CreateTrackAsync();
        using var firstResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-hold-t", new { content = "hold-timeout" });
        Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);
        var firstReceipt = (await firstResp.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        await firstTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var upResp = await UploadBinaryAsync(host.Client, $"/api/v1/sessions/{leadId}/attachments", SamplePng, "image/png");
        Assert.Equal(HttpStatusCode.Created, upResp.StatusCode);
        var att = (await upResp.Content.ReadFromJsonAsync<UploadAttachmentResponseDto>(PergaJsonDefaults.ApiOptions))!;

        // 1. Direct steer with hanging staging -> times out pre-RPC and settles as rejected (NEVER unconfirmed/unknown, SteerAsync never called).
        using (var directResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-direct-timeout", new
        {
            mode = "steer",
            turnId = firstReceipt.TurnId,
            attachmentIds = new[] { att.AttachmentId }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, directResp.StatusCode);
            var directReceipt = (await directResp.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
            Assert.Equal("rejected", directReceipt.Delivery);
        }
        Assert.Empty(host.AgentRuntime.SteeredMessages);

        // 2. Queued message steered while staging hangs -> returns cleanly to queued (NEVER unknown) and delivers once turn finishes!
        using var qResp = await PostWithKeyAsync(host.Client, $"/api/v1/sessions/{leadId}/messages", "k-q-timeout", new
        {
            mode = "queue",
            content = "follow up after staging timeout",
            attachmentIds = new[] { att.AttachmentId }
        });
        Assert.Equal(HttpStatusCode.OK, qResp.StatusCode);
        var qReceipt = (await qResp.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;

        using (var steerQResp = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{leadId}/messages", new
        {
            mode = "steer",
            turnId = firstReceipt.TurnId,
            messageId = qReceipt.MessageId
        }, PergaJsonDefaults.ApiOptions))
        {
            Assert.Equal(HttpStatusCode.OK, steerQResp.StatusCode);
            var steerQReceipt = (await steerQResp.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
            Assert.Equal("queued", steerQReceipt.Delivery);
        }
        Assert.Empty(host.AgentRuntime.SteeredMessages);

        // Restore staging and finish turn 1 -> queued message promotes and delivers with its image!
        stageShouldHang = false;
        releaseFirstTurn.TrySetResult();
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);

        Assert.Equal(2, host.AgentRuntime.ExecutedTurns.Count);
        var promotedTurn = host.AgentRuntime.ExecutedTurns.Last();
        Assert.Equal("follow up after staging timeout", promotedTurn.Input.Prompt);
        Assert.Single(promotedTurn.Input.Images!);
    }

    private sealed class DelayableStageFileSystem(Func<bool> shouldHang) : IExecutionFileSystem
    {
        public Task<HostListDirResultDto> ListDirectoryAsync(string? path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.ListDirectoryAsync(path, ct);
        public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.DirectoryExistsAsync(path, ct);
        public Task<string> ResolveCanonicalPathAsync(string path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.ResolveCanonicalPathAsync(path, ct);
        public Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(string workspaceRoot, string? relativePath, int maxBytes, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.BrowseOrReadWorkspaceFileAsync(workspaceRoot, relativePath, maxBytes, ct);
        public Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.OpenWorkspaceRawFileAsync(workspaceRoot, relativePath, ct);

        public async Task<string> StageAttachmentAsync(string trackId, string attachmentId, string extension, string localPhysicalPath, string sha256, CancellationToken ct = default)
        {
            if (shouldHang())
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return localPhysicalPath;
        }
    }

    private sealed class ToggleableStageFileSystem(Func<bool> shouldFail) : IExecutionFileSystem
    {
        public Task<HostListDirResultDto> ListDirectoryAsync(string? path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.ListDirectoryAsync(path, ct);
        public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.DirectoryExistsAsync(path, ct);
        public Task<string> ResolveCanonicalPathAsync(string path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.ResolveCanonicalPathAsync(path, ct);
        public Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(string workspaceRoot, string? relativePath, int maxBytes, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.BrowseOrReadWorkspaceFileAsync(workspaceRoot, relativePath, maxBytes, ct);
        public Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(string workspaceRoot, string relativePath, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.OpenWorkspaceRawFileAsync(workspaceRoot, relativePath, ct);

        public Task<string> StageAttachmentAsync(string trackId, string attachmentId, string extension, string localPhysicalPath, string sha256, CancellationToken ct = default)
        {
            if (shouldFail())
            {
                throw new IOException("Simulated SSH staging transport error");
            }
            return Task.FromResult(localPhysicalPath);
        }
    }

    private static async Task<HttpResponseMessage> UploadBinaryAsync(
        HttpClient client,
        string url,
        byte[] bytes,
        string mediaType,
        string? filename = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(bytes)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        if (filename is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Attachment-Filename", filename);
        }

        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> PostWithKeyAsync(
        HttpClient client,
        string url,
        string key,
        object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: PergaJsonDefaults.ApiOptions)
        };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(req);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(expectedCode, doc.RootElement.GetProperty("code").GetString());
    }

    private sealed class PushStreamContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(data, 0, data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false; // Forces chunked streaming without Content-Length header
        }
    }
}
