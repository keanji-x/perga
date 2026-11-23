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
using Perga.Host.Configuration;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Host.Mcp;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class McpPluginsPolicyGuardAndLifecycleIntegrationTests
{
    [Fact]
    public async Task ChangedPolicyDataPreventsDispatchAfterColdPluginInitialization()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var cluster = new PergaHostTestCluster();
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate("""
            export default defineTemplate({
              id: 'plugin-init-policy', title: 'Plugin initialization policy',
              data: { initial: { locked: false } },
              before(action, ctx) {
                return action.type === 'tool.update' && ctx.data.locked ? Deny('Updates locked') : Allow();
              }
            });
            """);
        var area = await kernel.CreateAreaAsync(new("Plugin policy", DefaultCwd: cluster.DataDir), ct);
        var track = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "plugin-init-policy",
            EnabledPlugins: ["test-plugin"]), ct)).Track;
        var caller = await kernel.OpenMcpCallerAsync(track.LeadSessionId!, ct);
        var upstream = new PausedPluginInitialization();
        using var httpClient = new HttpClient(upstream);
        await using var router = new McpPluginRouter(kernel, new PergaHostOptions(), httpClient);
        router.RegisterPlugin(new("test-plugin", "http", "http://plugin.test/mcp"));
        var backend = router.TryCreateBackend("test-plugin")!;

        var call = backend.CallToolAsync(caller, "update", McpToolResultFormatter.EmptyObject, ct);
        try
        {
            await upstream.Initializing.Task.WaitAsync(ct);
            await kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.DataPatch,
                McpToolResultFormatter.ParseClone("""{"patch":{"locked":true}}"""), ct);
        }
        finally
        {
            upstream.Resume.TrySetResult();
        }

        var result = await call.WaitAsync(ct);
        Assert.True(result.IsError);
        Assert.Equal("Active policy changed after authorization.", Assert.Single(result.Content).Text);
        Assert.Equal(0, upstream.ToolCalls);

        var retry = await backend.CallToolAsync(caller, "update", McpToolResultFormatter.EmptyObject, ct);
        Assert.True(retry.IsError);
        Assert.Equal("Updates locked", Assert.Single(retry.Content).Text);
        Assert.Equal(0, upstream.ToolCalls);
    }

    [Theory]
    [InlineData(false, "test-plugin")]
    [InlineData(true, "test-plugin")]
    [InlineData(false, "perga")]
    [InlineData(true, "PeRgA")]
    [InlineData(false, "vcs")]
    [InlineData(true, "VcS")]
    public async Task DisabledPluginCannotDispatchAfterApprovalOrClientInitialization(bool duringApproval, string pluginId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (track, lead) = await host.CreateTrackAsync();
        await kernel.PatchTrackAsync(track, new(EnabledPlugins: [pluginId]), ct);
        var caller = await kernel.OpenMcpCallerAsync(lead, ct);
        if (duringApproval)
            await kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackSet, JsonSerializer.SerializeToElement(new
            {
                source = "export default {id:'guard',title:'Guard',before:a=>a.toolName==='update'?AskHuman('Update?'):Allow()};"
            }), ct);

        var upstream = new PausedPluginInitialization();
        using var client = new HttpClient(upstream);
        await using var router = new McpPluginRouter(kernel, new PergaHostOptions(), client);
        router.RegisterPlugin(new(pluginId, "http", "http://plugin.test/mcp"));
        var backend = router.TryCreateBackend(pluginId)!;
        var pending = backend.CallToolAsync(caller, "update", McpToolResultFormatter.EmptyObject, ct);
        try
        {
            if (duringApproval)
            {
                var approvals = (await kernel.GetTrackAggregateAsync(track, ct)).PendingApprovals;
                while (approvals.Count == 0)
                {
                    await Task.Delay(5, ct);
                    approvals = (await kernel.GetTrackAggregateAsync(track, ct)).PendingApprovals;
                }
                var approval = Assert.Single(approvals);
                await kernel.PatchTrackAsync(track, new(EnabledPlugins: []), ct);
                await kernel.RespondInteractionAsync(lead, approval.Id, new(ExecutionId: approval.ExecutionId, OptionId: "approve"),
                    new InteropPrincipal.Human(), ct);
            }
            else
            {
                await upstream.Initializing.Task.WaitAsync(ct);
                await kernel.PatchTrackAsync(track, new(EnabledPlugins: []), ct);
            }
            Assert.Contains(pluginId, caller.EnabledPlugins);
        }
        finally { upstream.Resume.TrySetResult(); }

        var result = await pending.WaitAsync(ct);
        Assert.True(result.IsError);
        Assert.Contains("is not enabled", Assert.Single(result.Content).Text);
        Assert.Equal(0, upstream.ToolCalls);
        if (duringApproval) Assert.False(upstream.Initializing.Task.IsCompleted);
    }

    [Fact]
    public async Task StdioPluginRechecksMembershipAfterAcquiringWriteLock()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (track, lead) = await host.CreateTrackAsync();
        await kernel.PatchTrackAsync(track, new(EnabledPlugins: ["locked"]), ct);
        var caller = await kernel.OpenMcpCallerAsync(lead, ct);
        var script = Path.Combine(host.DataDir, "locked-plugin.mjs");
        var marker = Path.Combine(host.DataDir, "plugin-called");
        await File.WriteAllTextAsync(script, """
            import { createInterface } from 'node:readline';
            import { writeFileSync } from 'node:fs';
            for await (const line of createInterface({input: process.stdin})) {
              const msg = JSON.parse(line);
              if (msg.id === undefined) continue;
              let result = {};
              if (msg.method === 'tools/list') result = {tools: []};
              if (msg.method === 'tools/call') {
                writeFileSync(process.argv[2], 'called');
                result = {content: [{type: 'text', text: 'called'}], isError: false};
              }
              process.stdout.write(JSON.stringify({jsonrpc: '2.0', id: msg.id, result}) + '\n');
            }
            """, ct);
        await using var router = new McpPluginRouter(kernel, new PergaHostOptions());
        router.RegisterPlugin(new("locked", "stdio", Command: ResolveNodeExecutable(), Args: [script, marker]));
        var backend = router.TryCreateBackend("locked")!;
        await backend.ListToolsAsync(caller, ct);

        // Hold the real client lock so the request is suspended inside WriteLineAsync.
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var clients = (System.Collections.IDictionary)typeof(McpPluginRouter).GetField("_clients", fields)!.GetValue(router)!;
        var upstream = clients["locked"]!;
        var writeLock = (SemaphoreSlim)upstream.GetType().GetField("_writeLock", fields)!.GetValue(upstream)!;
        var waiting = (System.Collections.IDictionary)upstream.GetType().GetField("_pending", fields)!.GetValue(upstream)!;
        await writeLock.WaitAsync(ct);
        Task<McpCallToolResult> call;
        try
        {
            call = backend.CallToolAsync(caller, "update", McpToolResultFormatter.EmptyObject, ct);
            while (waiting.Count == 0 && !call.IsCompleted)
                await Task.Delay(5, ct);
            Assert.False(call.IsCompleted);
            await kernel.PatchTrackAsync(track, new(EnabledPlugins: []), ct);
        }
        finally { writeLock.Release(); }

        var denied = await call.WaitAsync(ct);
        Assert.True(denied.IsError);
        Assert.Contains("is not enabled", Assert.Single(denied.Content).Text);
        Assert.False(File.Exists(marker));
    }

    private sealed class PausedPluginInitialization : HttpMessageHandler
    {
        public TaskCompletionSource Initializing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ToolCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var message = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = message.RootElement.GetProperty("method").GetString();
            if (method == "initialize")
            {
                Initializing.TrySetResult();
                await Resume.Task.WaitAsync(ct);
            }
            if (method == "tools/call")
                ToolCalls++;

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { result = new { } }) };
        }
    }

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
                                inputSchema = new { type = "object", properties = new { ticker = new { type = "string" } } },
                                annotations = new { readOnlyHint = true, destructiveHint = false, openWorldHint = true }
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
            using var ambientHost = new AmbientEnvironmentScope(new Dictionary<string, string?>
            {
                ["PERGA_TEST_MCP_DB"] = Path.Combine(tempPluginDir, "inherited.db"),
                ["ASPNETCORE_TEST_MCP_URL"] = "http://127.0.0.1:4240",
                ["VIRTUAL_ENV"] = "/simulated/venv"
            });
            var stdioScriptPath = Path.Combine(tempPluginDir, "stdio-calc.mjs");
            await File.WriteAllTextAsync(
                stdioScriptPath,
                """
                import readline from 'node:readline';
                if (Object.hasOwn(process.env, 'PERGA_TEST_MCP_DB') ||
                    Object.hasOwn(process.env, 'ASPNETCORE_TEST_MCP_URL') ||
                    process.env.VIRTUAL_ENV !== '/simulated/venv' ||
                    process.env.PLUGIN_SETTING !== 'configured') {
                  process.stderr.write('The plugin received the wrong environment.\n');
                  process.exit(1);
                }
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
                        args = new[] { stdioScriptPath },
                        env = new Dictionary<string, string> { ["PLUGIN_SETTING"] = "configured" }
                    }
                }
            });
            await File.WriteAllTextAsync(pluginsJsonPath, pluginsConfigJson, ct);

            await using var cluster = new PergaHostTestCluster(
                configureHostOptions: opts => opts.McpPluginsConfigPath = pluginsJsonPath);
            var client = cluster.AuthenticatedClient;
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
                      return AskHuman(`Approve ${call.mcpServer}:${call.toolName}?`, { approve: "Approve", deny: "Reject" });
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

            // Verify stdio-calc tools/list (omits annotations when upstream provides none) and tools/call work end-to-end!
            var stdioToolEntries = await McpKernelAndVcsIntegrationTests.ListToolsAsync(cluster, "/mcp/p/stdio-calc", token, ct);
            var stdioTools = stdioToolEntries.Keys.ToArray();
            Assert.Contains("calc_sum", stdioTools);
            Assert.False(stdioToolEntries["calc_sum"].TryGetProperty("annotations", out _));

            var (sumText, sumErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/p/stdio-calc",
                token,
                "calc_sum",
                new { a = 19, b = 23, mode = "allow" },
                ct);
            Assert.False(sumErr, sumText);
            Assert.Equal("SUM=42", sumText);

            // 4. Test PolicyVm.GuardToolCall -> Allow on HTTP plugin ("http-quote"); do NOT forward untrusted plugin approval hints
            var httpToolEntries = await McpKernelAndVcsIntegrationTests.ListToolsAsync(cluster, "/mcp/p/http-quote", token, ct);
            var httpTools = httpToolEntries.Keys.ToArray();
            Assert.False(httpToolEntries["quote_fetch"].TryGetProperty("annotations", out _));
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

            // 5. Verify policy denial for plugins, VCS, and Kernel writes, plus the Kernel read-only bypass.
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
                "status",
                new { mode = "deny" },
                ct);
            Assert.True(denyVcsErr);
            Assert.Contains("Policy blocked vcs:status", denyVcsText, StringComparison.Ordinal);

            var (readOnlyKernelText, readOnlyKernelErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/kernel",
                token,
                KernelMcpToolNames.TaskList,
                new { mode = "deny" },
                ct);
            Assert.False(readOnlyKernelErr, readOnlyKernelText);

            var (denyKernelText, denyKernelErr) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster,
                "/mcp/kernel",
                token,
                KernelMcpToolNames.TrackRename,
                new { title = "Blocked Title", mode = "deny" },
                ct);
            Assert.True(denyKernelErr);
            Assert.Contains("Policy blocked perga:track_rename", denyKernelText, StringComparison.Ordinal);

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
                $"/api/v1/sessions/{approvalApprove.SessionId}/interactions/{approvalApprove.Id}/respond",
                new { executionId = approvalApprove.ExecutionId, optionId = "approve" },
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
                $"/api/v1/sessions/{approvalReject.SessionId}/interactions/{approvalReject.Id}/respond",
                new { executionId = approvalReject.ExecutionId, optionId = "deny" },
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
        string trackId;
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
                var client = cluster.AuthenticatedClient;
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
                      flow: [
                        {
                          id: "every-five-min",
                          when: { cron: "*/5 * * * *", tz: "UTC" },
                          do: { kind: "wake", session: "lead", prompt: "CRON_PULSE:${inputs.target}" }
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
                trackId = snap.RootElement.GetProperty("track").GetProperty("id").GetString()!;
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
                await kernel.WaitForTrackIdleAsync(trackId, ct);

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
                    CreatedAtMs: nowMs,
                    UpdatedAtMs: nowMs,
                    RunCount: 1,
                    Meta: dummyDoc.RootElement.Clone(),
                    StartedAtMs: nowMs);

                await store.UpsertTaskAsync(crashedTask, null, ct);
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
                Assert.Equal(1, bootSvc.LastReconcileResult.WokenTracks);

                var store = rebooted.Services.GetRequiredService<SqliteTrackStore>();
                var agg = await store.LoadTrackAggregateAsync(trackId, ct);
                var taskAfter = Assert.Single(agg!.Tasks, t => t.Id == "tsk_crash_1");
                Assert.Equal(TaskRunStatus.Failed, taskAfter.Status);
                Assert.NotNull(taskAfter.FinishedAtMs);
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
