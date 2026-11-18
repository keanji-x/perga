using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Perga.TermClient.Tests;

public sealed class TerminalBridgeE2ETests
{
    private const int RingBufferCapacityBytes = 64 * 1024;

    [Fact]
    public async Task TcpAndUds_EnsureIdempotency_Actions_Observe_Resize_And_Kill_WorkEndToEnd()
    {
        var tempDir = Directory.CreateTempSubdirectory("perga-term-e2e-");
        try
        {
            var udsPath = Path.Combine(tempDir.FullName, "bridge.sock");
            await using var daemon = await TerminalBridgeDaemon.StartAsync(new TerminalBridgeDaemonOptions(
                ListenUnixSocketPath: udsPath,
                ListenTcpAddress: "127.0.0.1:0",
                EnableLinuxUnshare: true));

            Assert.NotNull(daemon.TcpEndpoint);
            Assert.Equal(udsPath, daemon.UnixSocketPath);
            Assert.True(daemon.ProcessId > 0);

            await using var udsClient = daemon.CreateUnixSocketClient();
            await using var tcpClient = daemon.CreateTcpClient();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ct = cts.Token;

            // 1. Ensure via UDS, then verify idempotent Ensure via TCP.
            var ensureReq = new TermEnsureRequest(
                TerminalId: "t_e2e_1",
                Program: "/bin/sh",
                Args: [],
                Cwd: tempDir.FullName,
                Env: new Dictionary<string, string>
                {
                    ["PERGA_E2E_ENV"] = "hello_from_csharp"
                },
                Cols: 110,
                Rows: 32);

            var created = await udsClient.EnsureAsync(ensureReq, ct);
            Assert.Equal("t_e2e_1", created.TerminalId);
            Assert.True(created.Created);
            Assert.True(created.Pid > 0);

            var duplicate = await tcpClient.EnsureAsync(ensureReq, ct);
            Assert.Equal("t_e2e_1", duplicate.TerminalId);
            Assert.False(duplicate.Created);
            Assert.Equal(created.Pid, duplicate.Pid);

            // 2. Submit command and ObserveAsync with TermWaitMode.Text.
            var submitAck = await udsClient.SendActionAsync(
                new TermActionRequest("t_e2e_1", TermActionKind.Submit, TermActionPayload.Submit("printf 'ENV=%s\\n' \"$PERGA_E2E_ENV\"")),
                ct);
            Assert.True(submitAck.AckRevision >= 1);

            var observed = await tcpClient.ObserveAsync(
                new TermObserveRequest(
                    TerminalId: "t_e2e_1",
                    WaitFor: TermWaitMode.Text,
                    WaitMs: 5000,
                    SettleMs: 100,
                    WaitText: ["ENV=hello_from_csharp"]),
                ct);

            Assert.False(observed.Exited);
            Assert.Null(observed.ExitCode);
            Assert.Equal((ushort)110, observed.Frame.Cols);
            Assert.Equal((ushort)32, observed.Frame.Rows);
            Assert.Contains(observed.Frame.Text, line => line.Contains("ENV=hello_from_csharp", StringComparison.Ordinal));

            // 3. Test Text + Keys (Ctrl+U line clear + Enter) actions.
            await udsClient.SendActionAsync(
                new TermActionRequest("t_e2e_1", TermActionKind.Text, TermActionPayload.Text("discarded_draft")),
                ct);
            await udsClient.SendActionAsync(
                new TermActionRequest("t_e2e_1", TermActionKind.Keys, TermActionPayload.Keys("Ctrl+U")),
                ct);
            await udsClient.SendActionAsync(
                new TermActionRequest("t_e2e_1", TermActionKind.Text, TermActionPayload.Text("printf 'KEY_OKZ\\n'")),
                ct);
            var keyAck = await udsClient.SendActionAsync(
                new TermActionRequest("t_e2e_1", TermActionKind.Keys, TermActionPayload.Keys("Enter")),
                ct);
            Assert.True(keyAck.AckRevision > submitAck.AckRevision);

            var afterKeys = await udsClient.ObserveAsync(
                new TermObserveRequest(
                    TerminalId: "t_e2e_1",
                    WaitFor: TermWaitMode.Text,
                    WaitMs: 5000,
                    SettleMs: 100,
                    WaitText: ["KEY_OKZ"]),
                ct);
            Assert.Contains(afterKeys.Frame.Text, line => line.Contains("KEY_OKZ", StringComparison.Ordinal));

            // 4. Test Paste with MODE_BRACKETPASTE enabled on the terminal surface (-icanon reads raw bytes without waiting for LF).
            await udsClient.SendActionAsync(
                new TermActionRequest(
                    "t_e2e_1",
                    TermActionKind.Submit,
                    TermActionPayload.Submit("stty -icanon -echo; printf '\\033[?2004hBP_ARMED\\n'; dd bs=1 count=15 2>/dev/null | od -An -tx1 | tr -d ' \\n'; stty sane; printf '\\n\\033[?2004l\\n'")),
                ct);

            var bpArmed = await udsClient.ObserveAsync(
                new TermObserveRequest(
                    TerminalId: "t_e2e_1",
                    WaitFor: TermWaitMode.Text,
                    WaitMs: 5000,
                    SettleMs: 100,
                    WaitText: ["BP_ARMED"]),
                ct);
            Assert.NotEqual(0u, bpArmed.Frame.Modes);

            await udsClient.SendActionAsync(
                new TermActionRequest("t_e2e_1", TermActionKind.Paste, TermActionPayload.Paste("abc")),
                ct);

            // "\x1b[200~abc\x1b[201~" is 6 + 3 + 6 = 15 bytes -> hex "1b5b3230307e6162631b5b3230317e"
            var bpObserved = await udsClient.ObserveAsync(
                new TermObserveRequest(
                    TerminalId: "t_e2e_1",
                    WaitFor: TermWaitMode.Text,
                    WaitMs: 5000,
                    SettleMs: 100,
                    WaitText: ["1b5b3230307e6162631b5b3230317e"]),
                ct);
            Assert.Contains(
                bpObserved.Frame.Text,
                line => line.Contains("1b5b3230307e6162631b5b3230317e", StringComparison.Ordinal));

            // 5. ResizeAsync over RPC and verify via ObserveAsync + ScrollOffset history + WaitTextAbsent.
            await udsClient.SendActionAsync(
                new TermActionRequest(
                    "t_e2e_1",
                    TermActionKind.Submit,
                    TermActionPayload.Submit("i=1; while [ $i -le 45 ]; do printf 'HIST_ROW_%d\\n' \"$i\"; i=$((i+1)); done; printf 'SCROLL_READY\\n'")),
                ct);

            var scrollLive = await tcpClient.ObserveAsync(
                new TermObserveRequest(
                    TerminalId: "t_e2e_1",
                    WaitFor: TermWaitMode.Text,
                    WaitMs: 5000,
                    SettleMs: 100,
                    WaitText: ["SCROLL_READY"]),
                ct);
            Assert.True(scrollLive.Frame.HistoryRows > 0);

            var scrolledBack = await tcpClient.ObserveAsync(
                new TermObserveRequest("t_e2e_1", ScrollOffset: scrollLive.Frame.HistoryRows, WaitFor: TermWaitMode.None),
                ct);
            Assert.True(scrolledBack.Frame.ScrollOffset > 0);
            Assert.False(scrolledBack.Frame.Cursor.Visible);

            await tcpClient.ResizeAsync("t_e2e_1", 140, 45, ct);
            var resized = await udsClient.ObserveAsync(
                new TermObserveRequest("t_e2e_1", WaitFor: TermWaitMode.Elapsed, WaitMs: 25),
                ct);
            Assert.Equal((ushort)140, resized.Frame.Cols);
            Assert.Equal((ushort)45, resized.Frame.Rows);

            // 6. KillAsync terminates active shell and marks terminal Exited.
            await tcpClient.KillAsync("t_e2e_1", graceMs: 500, ct: ct);
            var postKill = await udsClient.ObserveAsync(
                new TermObserveRequest("t_e2e_1", WaitFor: TermWaitMode.None),
                ct);
            Assert.True(postKill.Exited);
            Assert.NotNull(postKill.ExitCode);
        }
        finally
        {
            TryDeleteDirectory(tempDir.FullName);
        }
    }

    [Fact]
    public async Task WebSocket_OverTcpAndUds_Replays64KbHistory_Streams0x01LiveBytes_And_Handles0x02ResizeAndExit()
    {
        var tempDir = Directory.CreateTempSubdirectory("perga-term-ws-");
        try
        {
            var udsPath = Path.Combine(tempDir.FullName, "ws-bridge.sock");
            await using var daemon = await TerminalBridgeDaemon.StartAsync(new TerminalBridgeDaemonOptions(
                ListenUnixSocketPath: udsPath,
                ListenTcpAddress: "127.0.0.1:0"));

            await using var tcpClient = daemon.CreateTcpClient();
            await using var udsClient = daemon.CreateUnixSocketClient();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var ct = cts.Token;

            var relayUri = tcpClient.GetWebSocketRelayUri("t_ws_1");
            Assert.Equal("ws", relayUri.Scheme);
            Assert.Equal("/ws/t_ws_1", relayUri.AbsolutePath);

            await tcpClient.EnsureAsync(
                new TermEnsureRequest("t_ws_1", "/bin/sh", [], tempDir.FullName, new Dictionary<string, string>(), 100, 30),
                ct);

            // Write ~80KB into the PTY so the 64KB ring buffer wraps and retains the latest 64KB.
            await tcpClient.SendActionAsync(
                new TermActionRequest(
                    "t_ws_1",
                    TermActionKind.Submit,
                    TermActionPayload.Submit("dd if=/dev/zero bs=1024 count=80 2>/dev/null | tr '\\0' 'X'; printf '\\nREPLAY_ANCHOR_64K\\n'")),
                ct);

            await tcpClient.ObserveAsync(
                new TermObserveRequest(
                    TerminalId: "t_ws_1",
                    WaitFor: TermWaitMode.Text,
                    WaitMs: 6000,
                    SettleMs: 120,
                    WaitText: ["REPLAY_ANCHOR_64K"]),
                ct);

            // Connect real ClientWebSocket over TCP using GetWebSocketRelayUri AND a second ClientWebSocket over UDS!
            using var tcpWs = new ClientWebSocket();
            // These endpoints are the local test daemon, independent of ambient HTTP proxies.
            using var loopbackHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            await tcpWs.ConnectAsync(relayUri, loopbackHttp, ct);

            using var udsWs = await udsClient.ConnectWebSocketAsync("t_ws_1", ct);

            // Both WebSockets must immediately receive the 64KB 0x01 replay frame.
            var (tcpFirstType, tcpFirstBytes) = await ReceiveFullFrameAsync(tcpWs, ct);
            Assert.Equal(WebSocketMessageType.Binary, tcpFirstType);
            Assert.Equal(TerminalWebSocketFrames.PtyDataPrefix, tcpFirstBytes[0]);
            Assert.Equal(RingBufferCapacityBytes, tcpFirstBytes.Length - 1);
            var tcpReplayText = Encoding.UTF8.GetString(tcpFirstBytes, 1, tcpFirstBytes.Length - 1);
            Assert.Contains("REPLAY_ANCHOR_64K", tcpReplayText, StringComparison.Ordinal);

            var (udsFirstType, udsFirstBytes) = await ReceiveFullFrameAsync(udsWs, ct);
            Assert.Equal(WebSocketMessageType.Binary, udsFirstType);
            Assert.Equal(TerminalWebSocketFrames.PtyDataPrefix, udsFirstBytes[0]);
            Assert.Equal(RingBufferCapacityBytes, udsFirstBytes.Length - 1);

            // Send 0x02 resize control frame over UDS WebSocket and verify dimension update.
            var resizeFrame = TerminalWebSocketFrames.EncodeResizeControl(136, 44);
            await udsWs.SendAsync(resizeFrame, WebSocketMessageType.Binary, endOfMessage: true, ct);

            await Task.Delay(75, ct);
            var afterWsResize = await tcpClient.ObserveAsync(
                new TermObserveRequest("t_ws_1", WaitFor: TermWaitMode.None),
                ct);
            Assert.Equal((ushort)136, afterWsResize.Frame.Cols);
            Assert.Equal((ushort)44, afterWsResize.Frame.Rows);

            // Send 0x01 live command over TCP WebSocket that outputs live text and exits with code 17.
            var liveInput = TerminalWebSocketFrames.EncodePtyText("printf 'LIVE_WS_STREAM_OK\\n'; exit 17\r");
            await tcpWs.SendAsync(liveInput, WebSocketMessageType.Binary, endOfMessage: true, ct);

            var liveBuilder = new StringBuilder();
            int? observedExitCode = null;

            while (observedExitCode is null)
            {
                var (msgType, frame) = await ReceiveFullFrameAsync(tcpWs, ct);
                if (msgType == WebSocketMessageType.Close)
                {
                    break;
                }

                Assert.Equal(WebSocketMessageType.Binary, msgType);
                Assert.NotEmpty(frame);

                if (frame[0] == TerminalWebSocketFrames.PtyDataPrefix)
                {
                    liveBuilder.Append(Encoding.UTF8.GetString(frame, 1, frame.Length - 1));
                }
                else if (frame[0] == TerminalWebSocketFrames.ControlJsonPrefix)
                {
                    using var doc = TerminalWebSocketFrames.ParseControlJson(frame);
                    if (doc.RootElement.GetProperty("type").GetString() == "exit")
                    {
                        observedExitCode = doc.RootElement.GetProperty("exit_code").GetInt32();
                    }
                }
            }

            Assert.Contains("LIVE_WS_STREAM_OK", liveBuilder.ToString(), StringComparison.Ordinal);
            Assert.Equal(17, observedExitCode);

            // Connecting a new WS after process exit must replay both 0x01 history AND 0x02 exit frame immediately.
            using var postExitWs = await tcpClient.ConnectWebSocketAsync("t_ws_1", ct);
            var (_, postHistory) = await ReceiveFullFrameAsync(postExitWs, ct);
            Assert.Equal(TerminalWebSocketFrames.PtyDataPrefix, postHistory[0]);

            var (_, postExitCtrl) = await ReceiveFullFrameAsync(postExitWs, ct);
            Assert.Equal(TerminalWebSocketFrames.ControlJsonPrefix, postExitCtrl[0]);
            using var postDoc = TerminalWebSocketFrames.ParseControlJson(postExitCtrl);
            Assert.Equal("exit", postDoc.RootElement.GetProperty("type").GetString());
            Assert.Equal(17, postDoc.RootElement.GetProperty("exit_code").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(tempDir.FullName);
        }
    }

    [Fact]
    public async Task ObserveWaitModes_Change_And_WaitTextAbsent_And_RootPostEndpoint_Work()
    {
        var tempDir = Directory.CreateTempSubdirectory("perga-term-wait-");
        try
        {
            await using var daemon = await TerminalBridgeDaemon.StartAsync(
                new TerminalBridgeDaemonOptions(ListenTcpAddress: "127.0.0.1:0"));
            await using var client = daemon.CreateTcpClient();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ct = cts.Token;

            await client.EnsureAsync(
                new TermEnsureRequest("t_wait_1", "/bin/sh", [], tempDir.FullName, new Dictionary<string, string>(), 80, 24),
                ct);

            // Trigger delayed output and wait via TermWaitMode.Change.
            await client.SendActionAsync(
                new TermActionRequest(
                    "t_wait_1",
                    TermActionKind.Submit,
                    TermActionPayload.Submit("sleep 0.1; printf 'PHASE_BUSY\\n'; sleep 0.25; printf '\\033[2J\\033[HPHASE_DONE\\n'")),
                ct);

            var changed = await client.ObserveAsync(
                new TermObserveRequest("t_wait_1", WaitFor: TermWaitMode.Change, WaitMs: 3000, SettleMs: 80),
                ct);
            Assert.True(changed.Revision > 0);

            // Wait until PHASE_DONE is present AND PHASE_BUSY is absent from the viewport.
            var settled = await client.ObserveAsync(
                new TermObserveRequest(
                    TerminalId: "t_wait_1",
                    WaitFor: TermWaitMode.Text,
                    WaitMs: 5000,
                    SettleMs: 100,
                    WaitText: ["PHASE_DONE"],
                    WaitTextAbsent: ["PHASE_BUSY"]),
                ct);

            Assert.Contains(settled.Frame.Text, line => line.Contains("PHASE_DONE", StringComparison.Ordinal));
            Assert.DoesNotContain(settled.Frame.Text, line => line.Contains("PHASE_BUSY", StringComparison.Ordinal));

            // Also verify `POST /` JSON-RPC root alias directly over HTTP.
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var rootResp = await http.PostAsync(
                daemon.TcpEndpoint!,
                new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"bridge.ping\",\"params\":{}}",
                    Encoding.UTF8,
                    "application/json"),
                ct);
            rootResp.EnsureSuccessStatusCode();
            var rootBody = await rootResp.Content.ReadAsStringAsync(ct);
            Assert.Contains("\"service\":\"perga-term-bridge\"", rootBody, StringComparison.Ordinal);

            await client.KillAsync("t_wait_1", graceMs: 300, ct: ct);
        }
        finally
        {
            TryDeleteDirectory(tempDir.FullName);
        }
    }

    [Fact]
    public async Task UnknownTerminal_ThrowsStructuredTerminalBridgeException()
    {
        await using var client = await TerminalBridgeDaemon.StartManagedClientAsync(
            new TerminalBridgeDaemonOptions(ListenTcpAddress: "127.0.0.1:0"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<TerminalBridgeException>(() =>
            client.ObserveAsync(new TermObserveRequest("missing_terminal"), cts.Token));

        Assert.Equal(-32001, ex.ErrorCode);
    }

    private static async Task<(WebSocketMessageType MessageType, byte[] Data)> ReceiveFullFrameAsync(
        WebSocket ws,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (WebSocketMessageType.Close, []);
                }

                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return (result.MessageType, ms.ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
