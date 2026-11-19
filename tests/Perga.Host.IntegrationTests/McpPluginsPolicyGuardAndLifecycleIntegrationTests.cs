using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class McpPluginsPolicyGuardAndLifecycleIntegrationTests
{
    [Fact]
    public async Task McpPluginRouter_HttpAndStdioPlugins_EnabledPluginsFence_And_PolicyGuard_Allow_Deny_AskHuman_WorkEndToEnd()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // 1. Start a real loopback Kestrel upstream HTTP MCP server for "http-quote" plugin!
        var httpUpstreamCalls = new ConcurrentQueue<string>();
        var upstreamBuilder = WebApplication.CreateSlimBuilder();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstreamApp = upstreamBuilder.Build();

        upstreamApp.MapPost("/mcp", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idElem))
            {
                return Results.Accepted();
            }

            var id = idElem.GetInt64();
            var method = root.GetProperty("method").GetString();
            ctx.Response.Headers["Mcp-Session-Id"] = "up_sess_1";

            return method switch
            {
                "initialize" => Results.Json(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "http-quote", version = "1.0.0" }
                    }
                }),
                "tools/list" => Results.Json(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        tools = new[]
                        {
                            new
                            {
                                name = "quote_fetch",
                                description = "Fetch stock quote",
                                inputSchema = new { type = "object", properties = new { ticker = new { type = "string" } } }
                            }
                        }
                    }
                }),
                "tools/call" => HandleUpstreamCall(root, id),
                _ => Results.Json(new { jsonrpc = "2.0", id, result = new { } })
            };

            IResult HandleUpstreamCall(JsonElement reqRoot, long rpcId)
            {
                var args = reqRoot.GetProperty("params").GetProperty("arguments");
                var ticker = args.TryGetProperty("ticker", out var t) ? t.GetString() ?? "N/A" : "N/A";
                httpUpstreamCalls.Enqueue(ticker);
                return Results.Json(new
                {
                    jsonrpc = "2.0",
                    id = rpcId,
                    result = new
                    {
                        content = new[] { new { type = "text", text = $"PRICE:{ticker}=188.50" } },
                        isError = false
                    }
                });
            }
        });

        await upstreamApp.StartAsync(ct);
        var upstreamUrl = $"{upstreamApp.Urls.First().TrimEnd('/')}/mcp";

        // 2. Write a real stdio JSON-RPC MCP child server script (executed via node) + mcp-plugins.json!
        var tempPluginDir = Directory.CreateTempSubdirectory("perga-mcp-plugins-").FullName;
        try
        {
            var stdioScriptPath = Path.Combine(tempPluginDir, "stdio-calc.mjs");
            await File.WriteAllTextAsync(
                stdioScriptPath,
                """
                import readline from 'node:readline';
                const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
                for await (const line of rl) {
                  const trimmed = line.trim();
                  if (!trimmed) continue;
                  const msg = JSON.parse(trimmed);
                  if (msg.id === undefined) continue;
                  if (msg.method === 'initialize') {
                    process.stdout.write(JSON.stringify({
                      jsonrpc: '2.0',
                      id: msg.id,
                      result: { protocolVersion: '2025-03-26', capabilities: { tools: {} }, serverInfo: { name: 'stdio-calc', version: '1.0.0' } }
                    }) + '\n');
                  } else if (msg.method === 'tools/list') {
                    process.stdout.write(JSON.stringify({
                      jsonrpc: '2.0',
                      id: msg.id,
                      result: { tools: [{ name: 'calc_sum', description: 'Add two numbers', inputSchema: { type: 'object' } }] }
                    }) + '\n');
                  } else if (msg.method === 'tools/call') {
                    const a = Number(msg.params?.arguments?.a ?? 0);
                    const b = Number(msg.params?.arguments?.b ?? 0);
                    process.stdout.write(JSON.stringify({
                      jsonrpc: '2.0',
                      id: msg.id,
                      result: { content: [{ type: 'text', text: `SUM=${a + b}` }], isError: false }
                    }) + '\n');
                  }
                }
                """,
                ct);

            var nodeExe = ResolveNodeExecutable();
            var pluginsJsonPath = Path.Combine(tempPluginDir, "mcp-plugins.json");
            var pluginsConfigJson = JsonSerializer.Serialize(new
            {
                plugins = new Dictionary<string, object>
                {
                    ["http-quote"] = new
                    {
                        transport = "http",
                        url = upstreamUrl
                    },
                    ["stdio-calc"] = new
                    {
                        transport = "stdio",
                        command = nodeExe,
                        args = new[] { stdioScriptPath }
                    }
                }
            });
            await File.WriteAllTextAsync(pluginsJsonPath, pluginsConfigJson, ct);

            await using var cluster = new PergaHostTestCluster(
                configureHostOptions: opts => opts.McpPluginsConfigPath = pluginsJsonPath);
            var client = cluster.Client;
            var kernel = cluster.Services.GetRequiredService<IPergaKernel>();

            // Register a policy template exercising GuardToolCall Allow / Deny / AskHuman across MCP servers!
            kernel.Templates.RegisterTemplate(
                """
                export default defineTemplate({
                  id: "guarded-mcp-test",
                  title: "Guarded MCP Policy",
                  plugins: ["http-quote"],
                  before(call, ctx) {
                    const mode = call.arguments && call.arguments.mode;
                    if (mode === "deny") {
                      return Deny(`Policy blocked ${call.mcpServer}:${call.toolName}`);
                    }
                    if (mode === "ask") {
                      return AskHuman(`Approve ${call.mcpServer}:${call.toolName}?`, ["Approve", "Reject"]);
                    }
                    return Allow();
                  }
                });
                """);

            var wsDir = Path.Combine(cluster.DataDir, "plugin-track-ws");
            Directory.CreateDirectory(wsDir);

            using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "Plugins", defaultCwd = wsDir }, ct);
            var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, ct))!;

            // Initially only "http-quote" is enabled via template default ("stdio-calc" NOT enabled yet!).
            // Sending a message opens Lead's IAgentSession and mints its real HostMcpSessionBridge pg_<guid> token!
            using var trackResp = await client.PostAsJsonAsync(
                "/api/v1/tracks",
                new { areaId = area.Id, templateId = "guarded-mcp-test", cwd = wsDir },
                ct);
            trackResp.EnsureSuccessStatusCode();
            using var snap = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync(ct));
            var trackId = snap.RootElement.GetProperty("track").GetProperty("id").GetString()!;
            var leadSessionId = snap.RootElement.GetProperty("track").GetProperty("leadSessionId").GetString()!;

            await kernel.WaitForTrackIdleAsync(trackId, ct);
            using var initMsg = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
            {
                Content = JsonContent.Create(new { content = "Init lead session" })
            };
            initMsg.Headers.Add("Idempotency-Key", "init-guarded-mcp");
            (await client.SendAsync(initMsg, ct)).EnsureSuccessStatusCode();

            await kernel.WaitForTrackIdleAsync(trackId, ct);
            var openedLead = Assert.Single(cluster.AgentRuntime.OpenedSessions);
            var httpQuoteServer = Assert.IsType<Perga.AgentKit.McpServerConfig.HttpMcpServer>(openedLead.Spec.McpServers["http-quote"]);
            var token = httpQuoteServer.Headers["Authorization"]["Bearer ".Length..];

            // 3. Calling /mcp/p/stdio-calc while NOT in Track EnabledPlugins -> 403 Forbidden on list, isError:true on call!
            using var forbiddenListResp = await cluster.PostMcpAsync(
                "/mcp/p/stdio-calc",
                token,
                new { jsonrpc = "2.0", id = 1, method = "tools/list" },
                ct: ct);
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenListResp.StatusCode);

            // Patch track to enable BOTH "http-quote" and "stdio-calc"!
            using var enablePatchResp = await client.PatchAsJsonAsync(
                $"/api/v1/tracks/{trackId}",
                new { enabledPlugins = new[] { "http-quote", "stdio-calc" } },
                ct);
            enablePatchResp.EnsureSuccessStatusCode();

            // Verify stdio-calc tools/list and tools/call work end-to-end!
            var stdioTools = await McpKernelAndVcsIntegrationTests.ListToolNamesAsync(cluster, "/mcp/p/stdio-calc", token, ct);
            Assert.Contains("calc_sum", stdioTools);

            var (sumText, sumErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/p/stdio-calc",
                token,
                "calc_sum",
                new { a = 19, b = 23, mode = "allow" },
                ct);
            Assert.False(sumErr, sumText);
            Assert.Equal("SUM=42", sumText);

            // 4. Test PolicyVm.GuardToolCall -> Allow on HTTP plugin ("http-quote")
            var httpTools = await McpKernelAndVcsIntegrationTests.ListToolNamesAsync(cluster, "/mcp/p/http-quote", token, ct);
            Assert.Contains("quote_fetch", httpTools);

            var (allowText, allowErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/p/http-quote",
                token,
                "quote_fetch",
                new { ticker = "NVDA", mode = "allow" },
                ct);
            Assert.False(allowErr, allowText);
            Assert.Equal("PRICE:NVDA=188.50", allowText);
            Assert.Single(httpUpstreamCalls);

            // 5. Test PolicyVm.GuardToolCall -> Deny on HTTP plugin, /mcp/vcs, AND /mcp/kernel!
            var (denyPluginText, denyPluginErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/p/http-quote",
                token,
                "quote_fetch",
                new { ticker = "DENIED_TICKER", mode = "deny" },
                ct);
            Assert.True(denyPluginErr);
            Assert.Contains("Policy blocked http-quote:quote_fetch", denyPluginText, StringComparison.Ordinal);
            Assert.Single(httpUpstreamCalls); // Upstream was NEVER invoked!

            var (denyVcsText, denyVcsErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/vcs",
                token,
                "vcs_status",
                new { mode = "deny" },
                ct);
            Assert.True(denyVcsErr);
            Assert.Contains("Policy blocked vcs:vcs_status", denyVcsText, StringComparison.Ordinal);

            var (denyKernelText, denyKernelErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/kernel",
                token,
                "perga_task_ls",
                new { mode = "deny" },
                ct);
            Assert.True(denyKernelErr);
            Assert.Contains("Policy blocked perga:perga_task_ls", denyKernelText, StringComparison.Ordinal);

            // 6. Test PolicyVm.GuardToolCall -> AskHuman live HTTP suspension + WS approval.changed and the decision route!
            using var eventsWs = await cluster.ConnectEventsWebSocketAsync($"?since=0&trackId={trackId}", ct);
            while (true)
            {
                using var replayFrame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(eventsWs, ct);
                if (replayFrame.RootElement.GetProperty("kind").GetString() == "_replay_complete")
                {
                    break;
                }
            }

            // 6A. Approved AskHuman -> suspends HTTP call, publishes an approval invalidation, resumes & executes upstream on Approve!
            var suspendedApproveTask = McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/p/http-quote",
                token,
                "quote_fetch",
                new { ticker = "MSFT", mode = "ask" },
                ct);

            ApprovalDto approvalApprove;
            while (true)
            {
                using var wsEvt = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(eventsWs, ct);
                if (wsEvt.RootElement.GetProperty("kind").GetString() != KernelEventKinds.ApprovalsChanged)
                    continue;
                var view = await client.GetFromJsonAsync<TrackAggregateSnapshot>(
                    $"/api/v1/tracks/{trackId}", PergaJsonDefaults.ApiOptions, ct);
                var pending = view!.PendingApprovals.SingleOrDefault();
                if (pending is null)
                    continue;
                approvalApprove = pending;
                Assert.Null(pending.TurnId);
                break;
            }

            // Confirm the MCP HTTP request is genuinely still suspended before human answers!
            Assert.False(suspendedApproveTask.IsCompleted);
            Assert.Single(httpUpstreamCalls);

            using var approveResp = await client.PostAsJsonAsync(
                $"/api/v1/tracks/{trackId}/executions/{approvalApprove.ExecutionId}/approvals/{approvalApprove.Id}/decision",
                new { answers = new[] { "Approve" } },
                ct);
            approveResp.EnsureSuccessStatusCode();

            var (resumedText, resumedErr) = await suspendedApproveTask.WaitAsync(ct);
            Assert.False(resumedErr, resumedText);
            Assert.Equal("PRICE:MSFT=188.50", resumedText);
            Assert.Equal(2, httpUpstreamCalls.Count);

            // 6B. Rejected AskHuman -> suspends HTTP call, returns isError: true without calling upstream on Reject!
            var suspendedRejectTask = McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/p/http-quote",
                token,
                "quote_fetch",
                new { ticker = "TSLA", mode = "ask" },
                ct);

            ApprovalDto approvalReject;
            while (true)
            {
                using var wsEvt = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(eventsWs, ct);
                if (wsEvt.RootElement.GetProperty("kind").GetString() != KernelEventKinds.ApprovalsChanged)
                    continue;
                var view = await client.GetFromJsonAsync<TrackAggregateSnapshot>(
                    $"/api/v1/tracks/{trackId}", PergaJsonDefaults.ApiOptions, ct);
                var pending = view!.PendingApprovals.SingleOrDefault();
                if (pending is null)
                    continue;
                approvalReject = pending;
                Assert.Null(pending.TurnId);
                break;
            }

            using var rejectResp = await client.PostAsJsonAsync(
                $"/api/v1/tracks/{trackId}/executions/{approvalReject.ExecutionId}/approvals/{approvalReject.Id}/decision",
                new { answers = new[] { "Reject" } },
                ct);
            rejectResp.EnsureSuccessStatusCode();

            var (rejectedText, rejectedErr) = await suspendedRejectTask.WaitAsync(ct);
            Assert.True(rejectedErr);
            Assert.Contains("denied by human", rejectedText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, httpUpstreamCalls.Count); // Upstream STILL at 2!
        }
        finally
        {
            await upstreamApp.StopAsync(CancellationToken.None);
            if (Directory.Exists(tempPluginDir))
            {
                Directory.Delete(tempPluginDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HostedLifecycleServices_CronosSchedulerWake_MaintenanceGc_And_BootCrashReconciliation_WorkEndToEnd()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = cts.Token;

        var sharedPersistDir = Directory.CreateTempSubdirectory("perga-lifecycle-it-").FullName;
        try
        {
            // -------- Part 1: CronosScheduleHostedService + SqliteMaintenanceHostedService --------
            await using (var cluster = new PergaHostTestCluster(
                             explicitDataDir: sharedPersistDir,
                             configureHostOptions: opts =>
                             {
                                 opts.EnableScheduleService = true;
                                 opts.SchedulePollInterval = TimeSpan.FromMilliseconds(30);
                                 opts.EnableMaintenanceService = true;
                                 opts.MaintenanceInterval = TimeSpan.FromMilliseconds(40);
                             }))
            {
                var client = cluster.Client;
                var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
                var store = cluster.Services.GetRequiredService<ITrackStore>();

                // Seed 3 expired events (> 7 days old relative to cluster clock) so periodic MaintenanceHostedService prunes them!
                var oldTimestampMs = cluster.TimeProvider.GetUtcNow().AddDays(-10).ToUnixTimeMilliseconds();
                using var dummyDoc = JsonDocument.Parse("""{"stale":true}""");
                for (var i = 0; i < 3; i++)
                {
                    await store.AppendEventAsync(
                        new EventEnvelopeDto(null, null, "trk_old", KernelEventKinds.TrackUpdated, dummyDoc.RootElement.Clone(), oldTimestampMs + i),
                        ct);
                }

                // Register scheduled template with Cronos 5-field expression
                kernel.Templates.RegisterTemplate(
                    """
                    export default defineTemplate({
                      id: "scheduled-monitor",
                      title: "Scheduled Monitor",
                      schedules: [
                        {
                          id: "every-five-min",
                          cron: "*/5 * * * *",
                          timezone: "UTC",
                          wakePrompt: (ctx) => `CRON_PULSE:${ctx.inputs.target ?? "default"}`
                        }
                      ]
                    });
                    """);

                var wsDir = Path.Combine(sharedPersistDir, "sched-ws");
                Directory.CreateDirectory(wsDir);

                using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "Ops", defaultCwd = wsDir }, ct);
                var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, ct))!;

                using var trackResp = await client.PostAsJsonAsync(
                    "/api/v1/tracks",
                    new { areaId = area.Id, templateId = "scheduled-monitor", templateInputs = new { target = "prod-cluster" }, cwd = wsDir },
                    ct);
                trackResp.EnsureSuccessStatusCode();
                using var snap = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync(ct));
                var trackId = snap.RootElement.GetProperty("track").GetProperty("id").GetString()!;
                var leadSessionId = snap.RootElement.GetProperty("track").GetProperty("leadSessionId").GetString()!;

                // Advance TimeProvider by 6 minutes -> CronosScheduleHostedService wakes Lead!
                cluster.TimeProvider.Advance(TimeSpan.FromMinutes(6));

                var schedService = cluster.Services.GetRequiredService<CronosScheduleHostedService>();
                var maintService = cluster.Services.GetRequiredService<SqliteMaintenanceHostedService>();

                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                // The scheduler counter records enqueueing; the background turn can reach the runtime later.
                while (DateTimeOffset.UtcNow < deadline &&
                       (schedService.TriggeredWakeCount < 1 || maintService.CompletedPasses < 1 ||
                        !cluster.AgentRuntime.ExecutedTurns.Any(t => t.Input.Prompt.Contains("CRON_PULSE:prod-cluster", StringComparison.Ordinal))))
                {
                    await Task.Delay(25, ct);
                }

                Assert.True(schedService.TriggeredWakeCount >= 1);
                Assert.Contains(
                    cluster.AgentRuntime.ExecutedTurns,
                    t => t.Input.Prompt.Contains("CRON_PULSE:prod-cluster", StringComparison.Ordinal));
                Assert.True(maintService.CompletedPasses >= 1);

                // Seed an in-flight crashed "Running" turn + "Running" attempt directly in SQLite before shutting down!
                var nowMs = cluster.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                using var inputDoc = JsonDocument.Parse("""{"prompt":"in-flight before crash"}""");
                await store.RecordTurnAndItemsAsync(
                    new SessionTurnDto(
                        Id: "trn_crashed_1",
                        SessionId: leadSessionId,
                        TurnIndex: 99,
                        Epoch: 1,
                        TriggerKind: "user",
                        Input: inputDoc.RootElement.Clone(),
                        Status: "Running",
                        Usage: null,
                        ErrorText: null,
                        StartedAtMs: nowMs,
                        CompletedAtMs: null),
                    newItems: [],
                    updatedSession: null,
                    ct);

                var crashedTask = new TaskDto(
                    Id: "tsk_crash_1",
                    TrackId: trackId,
                    BlockId: "b_crash",
                    TaskKey: "crashed-job",
                    Generation: 1,
                    Kind: "subagent",
                    Provider: "codex",
                    Access: AccessMode.ReadWrite,
                    Ready: true,
                    DependsOn: [],
                    GoalOrCmd: "Mid-flight work",
                    Context: null,
                    Acceptance: null,
                    Gate: null,
                    Status: TaskRunStatus.Running,
                    StatusDetail: null,
                    SemanticVerdict: SemanticVerdict.None,
                    SemanticNote: null,
                    LatestAttemptId: "att_crash_1",
                    CreatedAtMs: nowMs,
                    UpdatedAtMs: nowMs);

                using var leaseDoc = JsonDocument.Parse(
                    JsonSerializer.Serialize(
                        new AttemptWorkspaceLease("direct", "att_crash_1", wsDir, false, dummyDoc.RootElement.Clone()),
                        PergaJsonDefaults.ApiOptions));
                var crashedAttempt = new TaskAttemptDto(
                    AttemptId: "att_crash_1",
                    TaskId: "tsk_crash_1",
                    TrackId: trackId,
                    SessionId: null,
                    Status: "Running",
                    WorkerResult: null,
                    Meta: dummyDoc.RootElement.Clone(),
                    WorkspaceLease: leaseDoc.RootElement.Clone(),
                    GateVerdict: null,
                    StartedAtMs: nowMs,
                    FinishedAtMs: null);

                await store.UpsertTaskAndAttemptAsync(crashedTask, crashedAttempt, null, null, ct);
            }

            // -------- Part 2: Reboot Perga.Host on same SQLite DB -> PergaBootHostedService reconciles crashed state! --------
            await using (var rebooted = new PergaHostTestCluster(explicitDataDir: sharedPersistDir))
            {
                // Accessing Client ensures Host StartAsync (including PergaBootHostedService) has run!
                using var healthResp = await rebooted.Client.GetAsync("/healthz", ct);
                Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);

                var bootSvc = rebooted.Services.GetRequiredService<PergaBootHostedService>();
                Assert.NotNull(bootSvc.LastReconcileResult);
                Assert.NotEmpty(bootSvc.LastReconcileResult!.AffectedTrackIds);
                Assert.Equal(1, bootSvc.LastReconcileResult.DiscardedLeases);
                Assert.Equal(1, bootSvc.LastReconcileResult.WokenTracks);

                var store = rebooted.Services.GetRequiredService<SqliteTrackStore>();
                var attempts = await store.ListAttemptsForTaskAsync("tsk_crash_1", ct);
                Assert.Single(attempts);
                Assert.Equal("interrupted", attempts[0].Status);
            }
        }
        finally
        {
            if (Directory.Exists(sharedPersistDir))
            {
                Directory.Delete(sharedPersistDir, recursive: true);
            }
        }
    }

    private static string ResolveNodeExecutable()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "node");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "node";
    }
}
