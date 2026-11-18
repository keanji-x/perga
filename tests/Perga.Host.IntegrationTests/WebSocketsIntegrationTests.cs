using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class WebSocketsIntegrationTests
{
    [Fact]
    public async Task EventsWebSocket_DeliversEventCommittedBetweenReplayReadAndLiveStream()
    {
        await using var cluster = new PergaHostTestCluster(configureExtraServices: services =>
            services.AddSingleton<ITrackReadStore>(sp =>
            {
                var proxy = DispatchProxy.Create<ITrackReadStore, ReplayGateStore>();
                ((ReplayGateStore)(object)proxy).Initialize(sp.GetRequiredService<ITrackStore>(), sp.GetRequiredService<IPergaKernel>());
                return proxy;
            }));
        var (trackId, _) = await cluster.CreateTrackAsync();
        var store = cluster.Services.GetRequiredService<ITrackStore>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        var cursor = await store.GetEventTipIdAsync(ct);

        using var socket = await cluster.ConnectEventsWebSocketAsync($"?since={cursor}&trackId={trackId}", ct);
        using var ready = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, ct);
        Assert.Equal("_replay_complete", ready.RootElement.GetProperty("kind").GetString());
        Assert.Equal(cursor, ready.RootElement.GetProperty("tipId").GetInt64());

        using var eventFrame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, ct);
        Assert.Equal(cursor + 1, eventFrame.RootElement.GetProperty("eventId").GetInt64());
        Assert.Equal(KernelEventKinds.TrackUpdated, eventFrame.RootElement.GetProperty("kind").GetString());
    }

    public class ReplayGateStore : DispatchProxy
    {
        private ITrackStore _inner = null!;
        private IPergaKernel _kernel = null!;
        private bool _published;

        public void Initialize(ITrackStore inner, IPergaKernel kernel)
        {
            _inner = inner;
            _kernel = kernel;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(ITrackReadStore.ListEventsSinceAsync) &&
                args is [long since, string trackId, int limit, CancellationToken ct] && !_published)
            {
                _published = true;
                return ReadAndPublishAsync(since, trackId, limit, ct);
            }

            return method!.Invoke(_inner, args);
        }

        private async Task<IReadOnlyList<EventEnvelopeDto>> ReadAndPublishAsync(
            long since, string trackId, int limit, CancellationToken ct)
        {
            var replay = await _inner.ListEventsSinceAsync(since, trackId, limit, ct);
            var track = await _inner.GetTrackAsync(trackId, ct);
            var next = new EventEnvelopeDto(since + 1, track!.AreaId, trackId, KernelEventKinds.TrackUpdated,
                JsonSerializer.SerializeToElement(track, PergaJsonDefaults.ApiOptions), track.UpdatedAtMs);
            await _inner.AppendEventAsync(next, ct);
            _kernel.EventBus.PublishPersistent(next);
            return replay;
        }
    }

    [Fact]
    public async Task EventsWebSocket_ReplaysPersistedEvents_EmitsReplayComplete_And_StreamsTransientDeltas()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var wsDir = Path.Combine(cluster.DataDir, "ws-events-dir");
        Directory.CreateDirectory(wsDir);

        using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "Events Area", defaultCwd = wsDir }, ct);
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, ct))!;

        using var trackResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new { areaId = area.Id, title = "Streaming Track", cwd = wsDir },
            ct);
        using var trackSnap = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync(ct));
        var trackId = trackSnap.RootElement.GetProperty("track").GetProperty("id").GetString()!;
        var leadSessionId = trackSnap.RootElement.GetProperty("track").GetProperty("leadSessionId").GetString()!;

        // Add a draft task block so task.updated is persisted in events table before WS connects.
        using var putResp = await client.PutAsJsonAsync(
            $"/api/v1/tracks/{trackId}/report/blocks/b_tsk1",
            new
            {
                docRev = 0,
                knownBlockRev = 0,
                kind = "task",
                payload = new
                {
                    taskKey = "draft-ro",
                    kind = "subagent",
                    access = "read_only",
                    ready = false,
                    goal = "Inspect docs"
                }
            },
            ct);
        putResp.EnsureSuccessStatusCode();

        // Connect WS with ?since=0&trackId={trackId}
        using var ws = await cluster.ConnectEventsWebSocketAsync($"?since=0&trackId={trackId}", ct);

        var replayedFrames = new List<JsonElement>();
        long? replayTipId = null;

        while (replayTipId is null)
        {
            using var frameDoc = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(ws, ct);
            var root = frameDoc.RootElement.Clone();
            if (root.GetProperty("kind").GetString() == "_replay_complete")
            {
                replayTipId = root.GetProperty("tipId").GetInt64();
                break;
            }

            replayedFrames.Add(root);
        }

        Assert.NotNull(replayTipId);
        Assert.True(replayTipId > 0);
        Assert.Contains(replayedFrames, f => f.GetProperty("kind").GetString() == KernelEventKinds.TrackUpdated);

        var taskUpdatedFrame = replayedFrames.First(f => f.GetProperty("kind").GetString() == KernelEventKinds.TaskUpdated);
        Assert.Equal(
            "read_only",
            taskUpdatedFrame.GetProperty("payload").GetProperty("tasks")[0].GetProperty("access").GetString());

        // Configure AgentRuntime to emit ThinkingDelta, ToolCallStarted, ToolCallCompleted, and TextDelta!
        cluster.AgentRuntime.SetTurnHandler(StreamRichDeltasAsync);

        static async IAsyncEnumerable<UnifiedAgentEvent> StreamRichDeltasAsync(
            HarnessLaunchSpec spec,
            TurnInput input,
            [EnumeratorCancellation] CancellationToken innerCt)
        {
            _ = spec;
            _ = input;
            await Task.Yield();
            innerCt.ThrowIfCancellationRequested();
            using var argsDoc = JsonDocument.Parse("""{"q":"perga"}""");
            yield return new UnifiedAgentEvent.ThinkingDelta("step-1-thinking");
            yield return new UnifiedAgentEvent.ToolCallStarted("call_1", "perga_task_ls", argsDoc.RootElement.Clone());
            yield return new UnifiedAgentEvent.ToolCallCompleted("call_1", "[]", IsError: false);
            yield return new UnifiedAgentEvent.TextDelta("live-text-chunk");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(100, 30, 0, 100_000));
        }

        using var sendMsgReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
        {
            Content = JsonContent.Create(new { content = "Stream live deltas" })
        };
        sendMsgReq.Headers.Add("Idempotency-Key", "ws-live-1");
        using var sendMsgResp = await client.SendAsync(sendMsgReq, ct);
        sendMsgResp.EnsureSuccessStatusCode();

        // Collect live frames until turn.completed arrives.
        var liveKinds = new List<(string Kind, bool HasEventId)>();
        var sessionStatuses = new List<string>();
        while (true)
        {
            using var liveDoc = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(ws, ct);
            var root = liveDoc.RootElement;
            var kind = root.GetProperty("kind").GetString()!;
            var hasEventId = root.GetProperty("eventId").ValueKind == JsonValueKind.Number;
            liveKinds.Add((kind, hasEventId));
            if (kind == KernelEventKinds.SessionUpdated &&
                root.GetProperty("payload").GetProperty("id").GetString() == leadSessionId)
                sessionStatuses.Add(root.GetProperty("payload").GetProperty("status").GetString()!);

            if (kind == KernelEventKinds.TurnCompleted)
            {
                break;
            }
        }

        Assert.Contains("queued", sessionStatuses);
        Assert.Contains("running", sessionStatuses.SkipWhile(status => status != "queued").Skip(1));
        Assert.Contains(liveKinds, k => k.Kind == KernelEventKinds.SessionThinkingDelta && !k.HasEventId);
        Assert.Contains(liveKinds, k => k.Kind == KernelEventKinds.SessionToolProgress && !k.HasEventId);
        Assert.Contains(liveKinds, k => k.Kind == KernelEventKinds.SessionTextDelta && !k.HasEventId);
        Assert.Contains(liveKinds, k => k.Kind == KernelEventKinds.TurnCompleted && k.HasEventId);
    }

    [Fact]
    public async Task EventsWebSocket_EmitsSnapshotRequired_WhenMissedExceeds2000_Or_WhenSinceWasPruned()
    {
        await using var cluster = new PergaHostTestCluster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var store = cluster.Services.GetRequiredService<ITrackStore>();
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();

        using var payloadDoc = JsonDocument.Parse("""{"n":1}""");
        var payload = payloadDoc.RootElement.Clone();

        // 1. Seed 5 old events at t0, then advance clock 8 days and run maintenance GC to prune them,
        //    after adding 1 recent event so earliestRetainedId > 1!
        var baseMs = cluster.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
        {
            await store.AppendEventAsync(
                new EventEnvelopeDto(null, null, "trk_gc", KernelEventKinds.TrackUpdated, payload, baseMs + i),
                ct);
        }

        cluster.TimeProvider.Advance(TimeSpan.FromDays(8));
        var recentMs = cluster.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await store.AppendEventAsync(
            new EventEnvelopeDto(null, null, "trk_gc", KernelEventKinds.TrackUpdated, payload, recentMs),
            ct);

        var gcStats = await kernel.RunMaintenancePassAsync(ct);
        Assert.Equal(5, gcStats.PrunedEvents);

        // Reconnecting with since=2 (which was pruned prior to earliest retained event 6!) -> _snapshot_required!
        using (var prunedWs = await cluster.ConnectEventsWebSocketAsync("?since=2", ct))
        {
            using var firstFrame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(prunedWs, ct);
            Assert.Equal("_snapshot_required", firstFrame.RootElement.GetProperty("kind").GetString());
            Assert.Equal(6, firstFrame.RootElement.GetProperty("tipId").GetInt64());
        }

        // 2. Append 2,005 recent events after event 6 -> reconnecting with since=6 (> 2,000 missed) -> _snapshot_required!
        for (var i = 0; i < 2_005; i++)
        {
            await store.AppendEventAsync(
                new EventEnvelopeDto(null, null, "trk_flood", KernelEventKinds.TrackUpdated, payload, recentMs + 1 + i),
                ct);
        }

        using (var overflowWs = await cluster.ConnectEventsWebSocketAsync("?since=6", ct))
        {
            using var firstFrame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(overflowWs, ct);
            Assert.Equal("_snapshot_required", firstFrame.RootElement.GetProperty("kind").GetString());
            var resumeId = firstFrame.RootElement.GetProperty("tipId").GetInt64();
            Assert.Equal(2011, resumeId);
            await store.AppendEventAsync(
                new EventEnvelopeDto(null, null, "trk_flood", KernelEventKinds.TrackUpdated, payload, recentMs), ct);
            using var resumed = await cluster.ConnectEventsWebSocketAsync($"?since={resumeId}", ct);
            using var next = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(resumed, ct);
            Assert.Equal(resumeId + 1, next.RootElement.GetProperty("eventId").GetInt64());
            using var ready = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(resumed, ct);
            Assert.Equal("_replay_complete", ready.RootElement.GetProperty("kind").GetString());
            var live = new EventEnvelopeDto(resumeId + 2, null, "trk_flood", KernelEventKinds.TrackUpdated, payload, recentMs);
            await store.AppendEventAsync(live, ct);
            kernel.EventBus.PublishPersistent(live);
            using var delivered = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(resumed, ct);
            Assert.Equal(live.EventId, delivered.RootElement.GetProperty("eventId").GetInt64());
        }
    }

    [Fact]
    public async Task TerminalWebSocketRelay_Relays0x01PtyData_And_0x02ResizeAndExitFrames_EndToEnd()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var termCwd = Path.Combine(cluster.DataDir, "term-ws-dir");
        Directory.CreateDirectory(termCwd);

        using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "Term WS", defaultCwd = termCwd }, ct);
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, ct))!;

        using var trackResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new { areaId = area.Id, title = "Terminal Relay Track", cwd = termCwd },
            ct);
        using var snap = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync(ct));
        var trackId = snap.RootElement.GetProperty("track").GetProperty("id").GetString()!;

        using var openResp = await client.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/terminals",
            new { title = "Relay PTY", program = "/bin/sh", cols = 100, rows = 30 },
            ct);
        openResp.EnsureSuccessStatusCode();
        var term = (await openResp.Content.ReadFromJsonAsync<TerminalDto>(PergaJsonDefaults.ApiOptions, ct))!;

        using var ws = await cluster.ConnectTerminalWebSocketAsync(term.Id, ct);

        // 1. Send 0x02 resize control frame over WS and verify via Kernel ObserveTerminalAsync!
        var resizeJsonBytes = Encoding.UTF8.GetBytes("""{"type":"resize","cols":128,"rows":38}""");
        var resizeFrame = new byte[1 + resizeJsonBytes.Length];
        resizeFrame[0] = 0x02;
        Buffer.BlockCopy(resizeJsonBytes, 0, resizeFrame, 1, resizeJsonBytes.Length);
        await ws.SendAsync(resizeFrame.AsMemory(), WebSocketMessageType.Binary, endOfMessage: true, ct);

        // 2. Send 0x01 raw PTY command + exit so we receive both 0x01 output bytes AND 0x02 exit frame!
        var cmdBytes = Encoding.UTF8.GetBytes("printf 'WS_RELAY_MAGIC_99\\n'; exit 0\n");
        var inputFrame = new byte[1 + cmdBytes.Length];
        inputFrame[0] = 0x01;
        Buffer.BlockCopy(cmdBytes, 0, inputFrame, 1, cmdBytes.Length);
        await ws.SendAsync(inputFrame.AsMemory(), WebSocketMessageType.Binary, endOfMessage: true, ct);

        var ptyOutputBuilder = new StringBuilder();
        var sawExitControl = false;
        int? exitCode = null;

        while (!sawExitControl)
        {
            var (msgType, payload) = await PergaHostTestCluster.ReceiveBinaryFrameAsync(ws, ct);
            if (msgType == WebSocketMessageType.Close || payload.Length == 0)
            {
                break;
            }

            Assert.Equal(WebSocketMessageType.Binary, msgType);
            if (payload[0] == 0x01)
            {
                ptyOutputBuilder.Append(Encoding.UTF8.GetString(payload, 1, payload.Length - 1));
            }
            else if (payload[0] == 0x02)
            {
                using var ctrlDoc = JsonDocument.Parse(payload.AsMemory(1));
                if (ctrlDoc.RootElement.GetProperty("type").GetString() == "exit")
                {
                    sawExitControl = true;
                    exitCode = ctrlDoc.RootElement.GetProperty("exit_code").GetInt32();
                }
            }
        }

        Assert.Contains("WS_RELAY_MAGIC_99", ptyOutputBuilder.ToString(), StringComparison.Ordinal);
        Assert.True(sawExitControl);
        Assert.Equal(0, exitCode);
    }
}
