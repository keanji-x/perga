using Microsoft.Data.Sqlite;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;

namespace Perga.Host.IntegrationTests;

public sealed class ReadReceiptIntegrationTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("unread")]
    public async Task EmptyReceiptTargetReturnsBadRequest(string action)
    {
        await using var cluster = new PergaHostTestCluster();
        var response = await cluster.AuthenticatedClient.PostAsJsonAsync($"/api/v1/read-receipts/track//{action}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var unknown = await cluster.AuthenticatedClient.PostAsJsonAsync($"/api/v1/read-receipts/track/a/b/{action}", new { });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task FailedWritesPublishNoEventAndTransientUpdatesDoNotEnterReplay()
    {
        await using var cluster = new PergaHostTestCluster();
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        var store = (SqliteTrackStore)cluster.Services.GetRequiredService<ITrackStore>();
        var tip = await store.GetEventTipIdAsync(default);
        using var events = kernel.EventBus.Subscribe();
        await using var conn = new SqliteConnection($"Data Source={store.Options.GetFullDbPath()};Pooling=False");
        await conn.OpenAsync();
        using var command = conn.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_receipt BEFORE INSERT ON read_receipts BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
        await command.ExecuteNonQueryAsync();
        var failed = await cluster.AuthenticatedClient.PostAsJsonAsync("/api/v1/read-receipts/track/t/read", new { readThroughMs = 100 });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.False(events.Reader.TryRead(out _));
        Assert.Empty((await store.GetReadReceiptsAsync()).Receipts);
        command.CommandText = "DROP TRIGGER reject_receipt;";
        await command.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.OK, (await cluster.AuthenticatedClient.PostAsync("/api/v1/read-receipts/track/t/unread", null)).StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var evt = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal("t", evt.TrackId);
        Assert.Null(evt.EventId);
        Assert.Equal(tip, await store.GetEventTipIdAsync(default));
        Assert.Empty(await store.ListEventsSinceAsync(tip));
    }

    [Fact]
    public async Task ReceiptsValidatePersistAcrossHostRestartAndBroadcastTransientEvents()
    {
        var dir = Directory.CreateTempSubdirectory("perga-receipts-").FullName;
        try
        {
            long baseline;
            await using (var cluster = new PergaHostTestCluster(explicitDataDir: dir))
            {
                var client = cluster.AuthenticatedClient;
                baseline = (await client.GetFromJsonAsync<ReadReceiptsResponseDto>("/api/v1/read-receipts", PergaJsonDefaults.ApiOptions))!.BaselineMs;
                var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
                using var events = kernel.EventBus.Subscribe();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var socket = await cluster.ConnectEventsWebSocketAsync(ct: timeout.Token);
                // The websocket starts with its hello frame before delivering events.
                using var hello = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, timeout.Token);
                var result = await client.PostAsJsonAsync("/api/v1/read-receipts/conversation/session-a/read", new { readThroughMs = 100 });
                Assert.Equal(HttpStatusCode.OK, result.StatusCode);
                var receipt = await result.Content.ReadFromJsonAsync<ReadReceiptDto>(PergaJsonDefaults.ApiOptions);
                Assert.Equal(new ReadReceiptDto("conversation", "session-a", 100, false), receipt);
                var evt = await events.Reader.ReadAsync(timeout.Token);
                Assert.Equal(KernelEventKinds.ReadReceiptUpdated, evt.Kind);
                Assert.Null(evt.EventId);
                Assert.Null(evt.TrackId);
                Assert.True(KernelEventKinds.IsTransient(evt.Kind));
                using var frame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, timeout.Token);
                Assert.Contains("read_receipt.updated", frame.RootElement.GetRawText());
                await client.PostAsync("/api/v1/read-receipts/conversation/session-a/unread", null);
                await client.PostAsJsonAsync("/api/v1/read-receipts/conversation/session-a/read", new { readThroughMs = 1 });
                var state = await client.GetFromJsonAsync<ReadReceiptsResponseDto>("/api/v1/read-receipts", PergaJsonDefaults.ApiOptions);
                Assert.Equal(receipt, Assert.Single(state!.Receipts));
                foreach (var path in new[] { "invalid/target/read", "track/%20/read", "track/target/read" })
                {
                    var invalid = await client.PostAsJsonAsync($"/api/v1/read-receipts/{path}", new { readThroughMs = -1 });
                    Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                }
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/read-receipts/invalid/target/unread", null)).StatusCode);
                // Null and omitted readThroughMs clear manual unread without advancing the watermark.
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/read-receipts/track/t/read", new { })).StatusCode);
            }
            await using var restarted = new PergaHostTestCluster(explicitDataDir: dir);
            var after = await restarted.AuthenticatedClient.GetFromJsonAsync<ReadReceiptsResponseDto>("/api/v1/read-receipts", PergaJsonDefaults.ApiOptions);
            Assert.Equal(baseline, after!.BaselineMs);
            Assert.Equal(100, Assert.Single(after.Receipts, r => r.TargetId == "session-a").ReadThroughMs);
        }
        finally { Directory.Delete(dir, true); }
    }
}
