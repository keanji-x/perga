using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class ImageAttachmentHttpTests
{
    [Fact]
    public async Task ImageOnlyMessageAndFirstPromptReplayAndRemainReadableAfterRestart()
    {
        using var data = new TestData();
        var image = Png(1);
        string trackId;
        string sessionId;
        string path;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        await using (var host = ImageHost(data.Path))
        {
            (trackId, sessionId) = await host.CreateTrackAsync();
            path = MessagePath(sessionId);
            var kernel = host.Services.GetRequiredService<IPergaKernel>();
            byte[]? dispatched = null;
            host.AgentRuntime.BeforeTurn = async (input, token) =>
            {
                if (input.Attachments is { Count: > 0 })
                    dispatched = await ReadImageAsync(Assert.Single(input.Attachments), token);
            };
            Assert.True((await PageAsync(host, sessionId, ct)).AttachmentsSupported);
            using var sent = await PostImagesAsync(host.AuthenticatedClient, path, "image", new { content = "" }, image);
            sent.EnsureSuccessStatusCode();
            var receipt = (await sent.Content.ReadFromJsonAsync<SessionMessageReceipt>(ct))!;
            await kernel.WaitForTrackIdleAsync(trackId, ct);
            Assert.Equal(image, dispatched);
            var stored = Assert.Single((await PageAsync(host, sessionId, ct)).Items, item => item.Kind == "user");
            Assert.Equal(receipt.MessageId, stored.Payload.GetProperty("inputId").GetString());
            Assert.Equal("image", stored.Payload.GetProperty("requestKey").GetString());
            using var replay = await PostImagesAsync(host.AuthenticatedClient, path, "image", new { }, image);
            var repeated = (await replay.Content.ReadFromJsonAsync<SessionMessageReceipt>(ct))!;
            Assert.Equal(receipt.MessageId, repeated.MessageId);
            Assert.True(repeated.Replay);

            var createPath = $"/api/v1/tracks/{trackId}/sessions";
            using var created = await PostImagesAsync(host.AuthenticatedClient, createPath, "create", new { title = "Images" }, image);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var creation = (await created.Content.ReadFromJsonAsync<SessionCreationReceipt>(ct))!;
            Assert.NotNull(creation.FirstInput);
            await kernel.WaitForTrackIdleAsync(trackId, ct);
            Assert.Equal(Id(image), (await PageAsync(host, creation.Session.Id, ct)).Items
                .Single(i => i.Kind == "user").Payload.GetProperty("attachments")[0].GetProperty("id").GetString());
            using var repeatedCreation = await PostImagesAsync(host.AuthenticatedClient, createPath, "create",
                new { title = "Images", prompt = "" }, image);
            Assert.Equal(HttpStatusCode.OK, repeatedCreation.StatusCode);
            Assert.Equal(creation.Session.Id, (await repeatedCreation.Content.ReadFromJsonAsync<SessionCreationReceipt>(ct))!.Session.Id);

            var orphan = MediaPath(host, trackId, Png(2));
            await File.WriteAllBytesAsync(orphan, Png(2), ct);
            using var missing = await host.AuthenticatedClient.GetAsync(MediaUrl(trackId, Png(2)), ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var unauthorized = await host.Client.GetAsync(MediaUrl(trackId, image), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Directory.CreateDirectory(Path.Combine(host.DataDir, "media", "trk_deleted"));
        }

        await using var restarted = ImageHost(data.Path);
        Assert.False(Directory.Exists(Path.Combine(data.Path, "media", "trk_deleted")));
        var page = await PageAsync(restarted, sessionId, ct);
        var user = Assert.Single(page.Items, item => item.Kind == "user");
        Assert.Equal("", user.Payload.GetProperty("content").GetString());
        Assert.Equal("image", user.Payload.GetProperty("requestKey").GetString());
        var attachment = user.Payload.GetProperty("attachments")[0].Deserialize<MessageAttachmentDto>(PergaJsonDefaults.ApiOptions)!;
        Assert.Equal(new MessageAttachmentDto(Id(image), "image/png", image.Length, MediaUrl(trackId, image)), attachment);
        using var file = await restarted.AuthenticatedClient.GetAsync(attachment.Url, ct);
        file.EnsureSuccessStatusCode();
        Assert.Equal("image/png", file.Content.Headers.ContentType!.MediaType);
        Assert.True(file.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromDays(365), file.Headers.CacheControl.MaxAge);
        Assert.Contains(file.Headers.CacheControl.Extensions, x => x.Name == "immutable");
        Assert.Equal("nosniff", Assert.Single(file.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("sandbox", Assert.Single(file.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(image, await file.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task PreflightAndConcurrentIdempotencyConflictDoNotWriteLosingImages()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var path = MessagePath(sessionId);
        Assert.False((await PageAsync(host, sessionId, default)).AttachmentsSupported);
        using var unsupported = await PostImagesAsync(host.AuthenticatedClient, path, "unsupported", new { }, Png(1));
        await AssertErrorAsync(unsupported, HttpStatusCode.Conflict, "attachments_unsupported");
        using var unsupportedCreation = await PostImagesAsync(host.AuthenticatedClient, $"/api/v1/tracks/{trackId}/sessions",
            "unsupported-create", new { }, Png(1));
        await AssertErrorAsync(unsupportedCreation, HttpStatusCode.Conflict, "attachments_unsupported");
        Assert.False(Directory.Exists(Path.Combine(host.DataDir, "media")));

        host.AgentRuntime.SupportsImages = true;
        var sends = new[] { Png(2), Png(3) }.Select(bytes =>
            PostImagesAsync(host.AuthenticatedClient, path, "race", new { mode = "queue" }, bytes)).ToArray();
        await Task.WhenAll(sends);
        using var first = await sends[0];
        using var second = await sends[1];
        var winner = first.IsSuccessStatusCode ? Png(2) : Png(3);
        var loser = first.IsSuccessStatusCode ? Png(3) : Png(2);
        await AssertErrorAsync(first.IsSuccessStatusCode ? second : first, HttpStatusCode.Conflict, "conflict");
        Assert.True(File.Exists(MediaPath(host, trackId, winner)));
        Assert.False(File.Exists(MediaPath(host, trackId, loser)));
        await using var db = await OpenDbAsync(host);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM track_media_refs WHERE track_id = @TrackId", new { TrackId = trackId }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM session_messages WHERE session_id = @SessionId", new { SessionId = sessionId }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdAcpSessionWithUnprobedImageCapabilityAllowsFirstImage(bool createSession)
    {
        await using var host = new PergaHostTestCluster(configureAgentRuntime: runtime =>
            runtime.ProviderStatuses =
            [
                new("codex", true, null, null, []),
                new("acp:antigravity", true, null, null, [], SupportsImages: null)
            ]);
        var (trackId, _) = await host.CreateTrackAsync();
        var createPath = $"/api/v1/tracks/{trackId}/sessions";
        var endpoint = createPath;
        object payload = new { provider = "acp:antigravity" };
        if (!createSession)
        {
            using var cold = await PostAsync(host.AuthenticatedClient, createPath, "cold-acp", JsonContent.Create(payload));
            cold.EnsureSuccessStatusCode();
            var session = (await cold.Content.ReadFromJsonAsync<SessionCreationReceipt>())!.Session;
            Assert.True((await PageAsync(host, session.Id, default)).AttachmentsSupported);
            endpoint = MessagePath(session.Id);
            payload = new { };
        }

        using var response = await PostImagesAsync(host.AuthenticatedClient, endpoint, "first-acp-image", payload, Png(1));
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SessionPrincipalRequestKeyIsAbsentFromHttpHistory()
    {
        await using var host = ImageHost();
        var (trackId, leadId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var caller = await kernel.OpenMcpCallerAsync(leadId);
        var child = (await kernel.CreateSessionAsync(trackId, new(), "key-visibility-child", new InteropPrincipal.Human(), default)).Receipt.Session;
        var (text, isError) = await McpKernelAndVcsIntegrationTests.CallToolAsync(host, "/mcp/kernel", caller.Token,
            KernelMcpToolNames.SessionSend, new { sessionId = child.Id, content = "From the lead", requestKey = "session-private" }, default);
        Assert.False(isError, text);
        var sent = JsonSerializer.Deserialize<SessionMessageReceipt>(text, PergaJsonDefaults.ApiOptions)!;
        await kernel.WaitForTrackIdleAsync(trackId);
        var item = Assert.Single((await PageAsync(host, child.Id, default)).Items, item => item.Kind == "user");
        Assert.Equal(sent.MessageId, item.Payload.GetProperty("inputId").GetString());
        Assert.False(item.Payload.TryGetProperty("requestKey", out _));
    }

    [Fact]
    public async Task PartialFileWriteAndCommitFailureCleanNewMediaWhileKeepingSharedContent()
    {
        await using var host = ImageHost();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var endpoint = MessagePath(sessionId);
        var existing = Png(1);
        using var saved = await PostImagesAsync(host.AuthenticatedClient, endpoint, "shared", new { }, existing);
        saved.EnsureSuccessStatusCode();
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);

        var fresh = Png(2);
        var blocked = Png(3);
        Directory.CreateDirectory(MediaPath(host, trackId, blocked));
        using var failed = await PostImagesAsync(host.AuthenticatedClient, endpoint, "partial", new { }, existing, fresh, blocked);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.False(File.Exists(MediaPath(host, trackId, fresh)));
        using var shared = await host.AuthenticatedClient.GetAsync(MediaUrl(trackId, existing));
        shared.EnsureSuccessStatusCode();
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(MediaPath(host, trackId, fresh))!),
            name => name.Contains(".tmp.", StringComparison.Ordinal));

        await using var db = await OpenDbAsync(host);
        await db.ExecuteAsync("""
            CREATE TABLE reject_attachment_commit (
                missing_track TEXT REFERENCES tracks(id) DEFERRABLE INITIALLY DEFERRED
            );
            CREATE TRIGGER fail_image_commit AFTER INSERT ON session_messages
              WHEN NEW.request_key = 'rollback'
              BEGIN INSERT INTO reject_attachment_commit VALUES ('no-such-track'); END;
            """);
        using var rolledBack = await PostImagesAsync(host.AuthenticatedClient, endpoint, "rollback", new { }, existing, fresh);
        Assert.Equal(HttpStatusCode.InternalServerError, rolledBack.StatusCode);
        Assert.False(File.Exists(MediaPath(host, trackId, fresh)));
        Assert.True(File.Exists(MediaPath(host, trackId, existing)));
        Assert.Equal(Id(existing), await db.QuerySingleAsync<string>("SELECT file_name FROM track_media_refs"));
        Assert.Equal("shared", await db.QuerySingleAsync<string>("SELECT request_key FROM session_messages WHERE session_id = @SessionId",
            new { SessionId = sessionId }));
        using var hidden = await host.AuthenticatedClient.GetAsync(MediaUrl(trackId, fresh));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var delete = await host.AuthenticatedClient.DeleteAsync($"/api/v1/tracks/{trackId}");
        delete.EnsureSuccessStatusCode();
        Assert.Empty(await db.QueryAsync<string>("SELECT file_name FROM track_media_refs"));
        Assert.False(Directory.Exists(Path.Combine(host.DataDir, "media", trackId)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImageSteeringSeparatesPrePromptFailureFromDeliveryAndQueueFallback(bool queued, bool decline)
    {
        await using var host = ImageHost();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        host.AgentRuntime.SetTurnHandler(TurnAsync);
        var endpoint = MessagePath(sessionId);
        using var opening = await PostAsync(host.AuthenticatedClient, endpoint, "opening", JsonContent.Create(new { content = "running" }));
        opening.EnsureSuccessStatusCode();
        await started.Task.WaitAsync(ct);
        var turnId = (await PageAsync(host, sessionId, ct)).ActiveTurn!.Id;
        var store = host.Services.GetRequiredService<ITrackStore>().SessionMessages!;
        var submitted = 0;
        var attempts = 0;
        var first = Png(1);
        var second = Png(2);
        host.AgentRuntime.ImageSteerHandler = async (input, beforeWrite, token) =>
        {
            attempts++;
            var image = Assert.Single(input.Attachments!);
            var bytes = await ReadImageAsync(image, token);
            if (bytes.SequenceEqual(first))
                throw new AttachmentDispatchException("attachment_unavailable", "Provider preparation failed.");
            Assert.Equal(second, bytes);
            await beforeWrite!(token);
            submitted++;
            return decline ? SteerDelivery.Declined : SteerDelivery.Accepted;
        };

        try
        {
            using var failed = await Steer(first, "image-failure");
            await AssertErrorAsync(failed, HttpStatusCode.Conflict, "attachment_unavailable");
            var persisted = await store.FindAsync(sessionId, "owner:installation", "image-failure", ct);
            Assert.Equal(queued ? "queued" : "rejected", persisted!.Delivery);
            if (queued)
            {
                var queue = (await PageAsync(host, sessionId, ct)).QueuedMessages;
                Assert.Equal("waiting", Assert.Single(queue).State);
                Assert.Equal(Id(first), Assert.Single(queue[0].Attachments!).Id);
            }

            using var next = await Steer(second, "image-next");
            next.EnsureSuccessStatusCode();
            var receipt = (await next.Content.ReadFromJsonAsync<SessionMessageReceipt>(ct))!;
            Assert.Equal(decline ? "queued" : "delivered", receipt.Delivery);
            Assert.Equal(2, attempts);
            Assert.Equal(1, submitted);
            if (!decline)
            {
                var page = await PageAsync(host, sessionId, ct);
                Assert.Equal(Id(second), page.Items.Single(i => i.Kind == "user" &&
                    i.Payload.TryGetProperty("steered", out _)).Payload.GetProperty("attachments")[0].GetProperty("id").GetString());
            }
        }
        finally
        {
            release.TrySetResult();
        }

        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId, ct);
        if (queued)
        {
            var followup = host.AgentRuntime.ExecutedTurns.Last().Input;
            Assert.Equal(decline ? 2 : 1, followup.Attachments!.Count);
            Assert.Equal(first, await ReadImageAsync(followup.Attachments[0], ct));
            if (decline) Assert.Equal(second, await ReadImageAsync(followup.Attachments[1], ct));
        }

        async Task<HttpResponseMessage> Steer(byte[] bytes, string key)
        {
            if (!queued)
                return await PostImagesAsync(host.AuthenticatedClient, endpoint, key, new { mode = "steer", turnId }, bytes);
            using var enqueued = await PostImagesAsync(host.AuthenticatedClient, endpoint, key, new { mode = "queue" }, bytes);
            enqueued.EnsureSuccessStatusCode();
            var message = (await enqueued.Content.ReadFromJsonAsync<SessionMessageReceipt>(ct))!;
            return await PostAsync(host.AuthenticatedClient, endpoint, null,
                JsonContent.Create(new { mode = "steer", turnId, messageId = message.MessageId }));
        }

        async IAsyncEnumerable<UnifiedAgentEvent> TurnAsync(HarnessLaunchSpec _, TurnInput input,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new UnifiedAgentEvent.TurnAccepted("provider-turn");
            if (input.Prompt == "running")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
    }

    [Fact]
    public async Task QueuedImageSteerRejectsWhenTaskCancellationRevokesWorkspaceBeforeDispatch()
    {
        await using var host = ImageHost();
        var (trackId, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var messages = host.Services.GetRequiredService<ITrackStore>().SessionMessages!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSteerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSteer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapterCalls = 0;
        host.AgentRuntime.SetTurnHandler((_, _, token) => HoldWorkerTurnAsync(token));
        host.AgentRuntime.ImageSteerHandler = async (_, beforeWrite, token) =>
        {
            Interlocked.Increment(ref adapterCalls);
            await beforeWrite!(token);
            firstSteerStarted.TrySetResult();
            await releaseFirstSteer.Task.WaitAsync(token);
            return SteerDelivery.Accepted;
        };

        try
        {
            var (worker, task) = await FlowFixture.StartWorkerAsync(kernel, trackId, "cancel-image-steer", "Hold worker", ct);
            await turnStarted.Task.WaitAsync(ct);
            var endpoint = MessagePath(worker.Id);
            var turnId = (await PageAsync(host, worker.Id, ct)).ActiveTurn!.Id;
            using var queued = await PostImagesAsync(host.AuthenticatedClient, endpoint, "waiting-image", new { mode = "queue" }, Png(1));
            queued.EnsureSuccessStatusCode();
            var messageId = (await queued.Content.ReadFromJsonAsync<SessionMessageReceipt>(ct))!.MessageId;
            var first = PostImagesAsync(host.AuthenticatedClient, endpoint, "first-steer", new { mode = "steer", turnId }, Png(2));
            await firstSteerStarted.Task.WaitAsync(ct);
            var waiting = PostAsync(host.AuthenticatedClient, endpoint, null, JsonContent.Create(new { mode = "steer", turnId, messageId }));
            while ((await messages.GetAsync(worker.Id, messageId, ct))!.Delivery != "dispatching")
                await Task.Delay(10, ct);
            var cancellation = kernel.CancelTaskAsync(trackId, null, task.Id, ct: ct);
            await turnCanceled.Task.WaitAsync(ct);
            releaseFirstSteer.TrySetResult();

            using var firstResponse = await first.WaitAsync(ct);
            firstResponse.EnsureSuccessStatusCode();
            using var response = await waiting.WaitAsync(ct);
            await AssertErrorAsync(response, HttpStatusCode.Conflict, "conflict");
            Assert.Contains("task workspace is no longer available", await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("interrupted", (await messages.GetAsync(worker.Id, messageId, ct))!.Delivery);
            Assert.Equal(1, Volatile.Read(ref adapterCalls));

            releaseTurn.TrySetResult();
            await cancellation.WaitAsync(ct);
        }
        finally
        {
            releaseFirstSteer.TrySetResult();
            releaseTurn.TrySetResult();
        }

        async IAsyncEnumerable<UnifiedAgentEvent> HoldWorkerTurnAsync([EnumeratorCancellation] CancellationToken token)
        {
            using var canceled = token.Register(() => turnCanceled.TrySetResult());
            yield return new UnifiedAgentEvent.TurnAccepted("worker-turn");
            turnStarted.TrySetResult();
            await releaseTurn.Task;
            yield return new UnifiedAgentEvent.TurnInterrupted();
        }
    }

    [Fact]
    public async Task MalformedMultipartSectionHeadersAndAliasesReturn400()
    {
        await using var host = ImageHost();
        var (_, sessionId) = await host.CreateTrackAsync();
        var content = new StringContent("--test\r\nThis is not a header\r\n\r\n{}\r\n--test--\r\n");
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=test");
        using var malformed = await PostAsync(host.AuthenticatedClient, MessagePath(sessionId), "malformed", content);
        await AssertErrorAsync(malformed, HttpStatusCode.BadRequest, "invalid_argument");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("{}"), "payload");
        form.Add(new ByteArrayContent(Png(1)), "files", "alias.png");
        using var alias = await PostAsync(host.AuthenticatedClient, MessagePath(sessionId), "alias", form);
        await AssertErrorAsync(alias, HttpStatusCode.BadRequest, "invalid_argument");
    }

    [Fact]
    public async Task MultipartMessageRequiresAHeaderKeyAndRejectsQueuedMessageId()
    {
        await using var host = ImageHost();
        var (_, sessionId) = await host.CreateTrackAsync();
        var endpoint = MessagePath(sessionId);
        using var missingKey = await PostAsync(host.AuthenticatedClient, endpoint, null, ImageForm(new { }, [Png(1)]));
        await AssertErrorAsync(missingKey, HttpStatusCode.BadRequest, "invalid_argument");
        using var invalid = await PostImagesAsync(host.AuthenticatedClient, endpoint, "unexpected-message-id",
            new { messageId = "queued" }, Png(1));
        await AssertErrorAsync(invalid, HttpStatusCode.BadRequest, "invalid_json");
    }

    [Fact]
    public async Task KestrelStreamingLimitPreserves413AndProtocolErrorCode()
    {
        using var data = new TestData();
        await using var factory = new IsolatedPergaHostFactory(data.Path, configureHost: builder =>
            builder.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024));
        factory.UseKestrel(0);
        using var original = factory.CreateClient();
        using var client = await BrowserAuthentication.CreateAuthenticatedClientAsync(factory, original.BaseAddress);
        using var form = ImageForm(new { }, [new byte[48 * 1024]]);
        using var chunked = new ChunkedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, MessagePath("unused")) { Content = chunked };
        request.Headers.Add("Idempotency-Key", "streaming-413");
        request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
    }

    private static PergaHostTestCluster ImageHost(string? data = null) =>
        new(data, configureAgentRuntime: runtime => runtime.SupportsImages = true);

    private static string MessagePath(string sessionId) => $"/api/v1/sessions/{sessionId}/messages";
    private static byte[] Png(byte variant) => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, variant];
    private static string Id(byte[] bytes) => $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}.png";
    private static string MediaPath(PergaHostTestCluster host, string trackId, byte[] bytes) =>
        Path.Combine(host.DataDir, "media", trackId, Id(bytes));
    private static string MediaUrl(string trackId, byte[] bytes) => $"/api/v1/tracks/{trackId}/media/{Id(bytes)}";

    private static async Task<SessionItemsPageDto> PageAsync(PergaHostTestCluster host, string sessionId, CancellationToken ct) =>
        (await host.AuthenticatedClient.GetFromJsonAsync<SessionItemsPageDto>(
            $"/api/v1/sessions/{sessionId}/items", PergaJsonDefaults.ApiOptions, ct))!;

    private static async Task<byte[]> ReadImageAsync(TurnImage image, CancellationToken ct)
    {
        await using var stream = await image.OpenAsync(ct);
        Assert.NotNull(stream);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return output.ToArray();
    }

    private static async Task<SqliteConnection> OpenDbAsync(PergaHostTestCluster host)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(host.DataDir, "perga.db"), Pooling = false }.ConnectionString);
        await db.OpenAsync();
        return db;
    }

    private static MultipartFormDataContent ImageForm(object payload, byte[][] images)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8), "payload");
        foreach (var image in images)
            form.Add(new ByteArrayContent(image), "attachments", "ignored-name");
        return form;
    }

    private static Task<HttpResponseMessage> PostImagesAsync(HttpClient client, string path, string key,
        object payload, params byte[][] images) => PostAsync(client, path, key, ImageForm(payload, images));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? key, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private sealed class TestData : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("perga-image-tests-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ChunkedContent : HttpContent
    {
        private readonly HttpContent _inner;
        public ChunkedContent(HttpContent content)
        {
            _inner = content;
            Headers.ContentType = content.Headers.ContentType;
        }
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _inner.CopyToAsync(stream);
    }
}
