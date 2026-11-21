using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Mcp;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class McpKernelAndVcsIntegrationTests
{
    [Fact]
    public async Task McpStreamableHttp_Auth_Initialize_Batch_PostSse_And_GetSseStream_WorkEndToEnd()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var wsDir = Path.Combine(cluster.DataDir, "mcp-proto-ws");
        Directory.CreateDirectory(wsDir);

        using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "MCP Proto", defaultCwd = wsDir }, ct);
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, ct))!;

        using var trackResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new
            {
                areaId = area.Id,
                title = "MCP Streamable Track",
                cwd = wsDir,
                enabledPlugins = new[] { "market-data" }
            },
            ct);
        trackResp.EnsureSuccessStatusCode();

        using var created = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync(ct));
        var createdTrackId = created.RootElement.GetProperty("track").GetProperty("id").GetString()!;
        var createdLeadId = created.RootElement.GetProperty("track").GetProperty("leadSessionId").GetString()!;
        await cluster.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(createdTrackId, ct);
        using var bootReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{createdLeadId}/messages")
        {
            Content = JsonContent.Create(new { content = "Boot lead session" })
        };
        bootReq.Headers.Add("Idempotency-Key", "boot-mcp-streamable");
        using var bootResp = await client.SendAsync(bootReq, ct);
        bootResp.EnsureSuccessStatusCode();
        await cluster.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(createdTrackId, ct);

        // Verify HarnessLaunchSpec captured Host-minted pg_<guid> token & MCP server URLs!
        var launched = Assert.Single(cluster.AgentRuntime.OpenedSessions);
        Assert.True(launched.Spec.McpServers.ContainsKey("perga"));
        Assert.True(launched.Spec.McpServers.ContainsKey("vcs"));
        Assert.True(launched.Spec.McpServers.ContainsKey("market-data"));

        var pergaServer = Assert.IsType<McpServerConfig.HttpMcpServer>(launched.Spec.McpServers["perga"]);
        Assert.EndsWith("/mcp/kernel", pergaServer.Url, StringComparison.Ordinal);
        var authHeader = pergaServer.Headers["Authorization"];
        Assert.StartsWith("Bearer pg_", authHeader, StringComparison.Ordinal);
        var pgToken = authHeader["Bearer ".Length..];

        // 1. Missing or invalid token -> 401 Unauthorized
        using var noAuthResp = await cluster.PostMcpAsync("/mcp/kernel", null, new { jsonrpc = "2.0", id = 1, method = "ping" }, ct: ct);
        Assert.Equal(HttpStatusCode.Unauthorized, noAuthResp.StatusCode);

        using var badTokenResp = await cluster.PostMcpAsync("/mcp/kernel", "pg_invalid_token", new { jsonrpc = "2.0", id = 1, method = "ping" }, ct: ct);
        Assert.Equal(HttpStatusCode.Unauthorized, badTokenResp.StatusCode);

        // 2. initialize -> returns serverInfo.name == "perga" + Mcp-Session-Id header
        using var initResp = await cluster.PostMcpAsync(
            "/mcp/kernel",
            pgToken,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-03-26", capabilities = new { } }
            },
            ct: ct);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        Assert.True(initResp.Headers.TryGetValues("Mcp-Session-Id", out var sessionIdValues));
        var mcpSessionId = sessionIdValues!.First();
        Assert.StartsWith("mcp_", mcpSessionId, StringComparison.Ordinal);

        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync(ct));
        Assert.Equal("perga", initDoc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        // 3. notifications/initialized -> 202 Accepted
        using var notifResp = await cluster.PostMcpAsync(
            "/mcp/kernel",
            pgToken,
            new { jsonrpc = "2.0", method = "notifications/initialized" },
            mcpSessionId: mcpSessionId,
            ct: ct);
        Assert.Equal(HttpStatusCode.Accepted, notifResp.StatusCode);

        // 4. Batch JSON-RPC request [ping, tools/list]
        using var batchResp = await cluster.PostMcpAsync(
            "/mcp/kernel",
            pgToken,
            new object[]
            {
                new { jsonrpc = "2.0", id = 10, method = "ping" },
                new { jsonrpc = "2.0", id = 11, method = "tools/list" }
            },
            mcpSessionId: mcpSessionId,
            ct: ct);
        Assert.Equal(HttpStatusCode.OK, batchResp.StatusCode);
        using var batchDoc = JsonDocument.Parse(await batchResp.Content.ReadAsStringAsync(ct));
        Assert.Equal(2, batchDoc.RootElement.GetArrayLength());

        // 5. POST with Accept: text/event-stream -> returns text/event-stream SSE!
        using var postSseResp = await cluster.PostMcpAsync(
            "/mcp/kernel",
            pgToken,
            new { jsonrpc = "2.0", id = 20, method = "ping" },
            mcpSessionId: mcpSessionId,
            acceptHeader: "text/event-stream",
            ct: ct);
        Assert.Equal(HttpStatusCode.OK, postSseResp.StatusCode);
        Assert.Equal("text/event-stream", postSseResp.Content.Headers.ContentType?.MediaType);
        var postSseBody = await postSseResp.Content.ReadAsStringAsync(ct);
        Assert.Contains("event: message", postSseBody, StringComparison.Ordinal);
        Assert.Contains("\"id\":20", postSseBody, StringComparison.Ordinal);

        // 6. GET /mcp/kernel SSE stream with Mcp-Session-Id + server notification push + DELETE session!
        using var getSseReq = new HttpRequestMessage(HttpMethod.Get, "/mcp/kernel");
        getSseReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pgToken);
        getSseReq.Headers.TryAddWithoutValidation("Mcp-Session-Id", mcpSessionId);
        using var getSseResp = await client.SendAsync(getSseReq, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, getSseResp.StatusCode);
        Assert.Equal("text/event-stream", getSseResp.Content.Headers.ContentType?.MediaType);

        await using var sseStream = await getSseResp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(sseStream);
        var commentLine = await reader.ReadLineAsync(ct);
        Assert.Equal(": connected", commentLine);

        var sessionMgr = cluster.Services.GetRequiredService<McpTransportSessionManager>();
        Assert.True(sessionMgr.PublishNotification(mcpSessionId, "notifications/tools/list_changed"));

        string? dataLine = null;
        while (dataLine is null)
        {
            var line = await reader.ReadLineAsync(ct);
            Assert.NotNull(line);
            if (line!.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataLine = line["data: ".Length..];
            }
        }

        Assert.Contains("notifications/tools/list_changed", dataLine, StringComparison.Ordinal);

        using var delReq = new HttpRequestMessage(HttpMethod.Delete, "/mcp/kernel");
        delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pgToken);
        delReq.Headers.TryAddWithoutValidation("Mcp-Session-Id", mcpSessionId);
        using var delResp = await client.SendAsync(delReq, ct);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);
    }

    [Fact]
    public async Task McpKernel_ToolVisibility_And_BoundAttemptFences_WorkEndToEnd()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = cts.Token;

        var wsDir = Path.Combine(cluster.DataDir, "mcp-kernel-ws");
        Directory.CreateDirectory(wsDir);

        using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "Kernel Tools", defaultCwd = wsDir }, ct);
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, ct))!;

        // Create target Track B (for cross-track mail) and primary Track A, then send a message so both Lead sessions open & mint real pg_<guid> tokens!
        using var trackBResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new { areaId = area.Id, title = "Peer Track B", cwd = wsDir },
            ct);
        using var snapB = JsonDocument.Parse(await trackBResp.Content.ReadAsStringAsync(ct));
        var trackBId = snapB.RootElement.GetProperty("track").GetProperty("id").GetString()!;
        var trackBLeadId = snapB.RootElement.GetProperty("track").GetProperty("leadSessionId").GetString()!;

        using var trackAResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new { areaId = area.Id, title = "Primary Track A", cwd = wsDir },
            ct);
        using var snapA = JsonDocument.Parse(await trackAResp.Content.ReadAsStringAsync(ct));
        var trackAId = snapA.RootElement.GetProperty("track").GetProperty("id").GetString()!;
        var trackALeadId = snapA.RootElement.GetProperty("track").GetProperty("leadSessionId").GetString()!;

        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackAId, ct);
        await kernel.WaitForTrackIdleAsync(trackBId, ct);
        using (var initB = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{trackBLeadId}/messages")
        {
            Content = JsonContent.Create(new { content = "Init Track B Lead" })
        })
        {
            initB.Headers.Add("Idempotency-Key", "init-track-b");
            (await client.SendAsync(initB, ct)).EnsureSuccessStatusCode();
        }
        using (var initA = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{trackALeadId}/messages")
        {
            Content = JsonContent.Create(new { content = "Init Track A Lead" })
        })
        {
            initA.Headers.Add("Idempotency-Key", "init-track-a");
            (await client.SendAsync(initA, ct)).EnsureSuccessStatusCode();
        }

        await kernel.WaitForTrackIdleAsync(trackAId, ct);
        await kernel.WaitForTrackIdleAsync(trackBId, ct);
        var hostBridge = cluster.Services.GetRequiredService<HostMcpSessionBridge>();
        var leadToken = hostBridge.GetTokenForSession(trackALeadId)!;
        var peerLeadToken = hostBridge.GetTokenForSession(trackBLeadId)!;

        // 1. Lead gets shared, governance and unified session tools.
        var leadTools = await ListToolNamesAsync(cluster, "/mcp/kernel", leadToken, ct);
        Assert.Equal(36, leadTools.Count);
        Assert.Contains(KernelMcpToolNames.ProviderList, leadTools);
        Assert.Contains(KernelMcpToolNames.DataPatch, leadTools);
        Assert.Contains(KernelMcpToolNames.TaskAccept, leadTools);
        Assert.Contains(KernelMcpToolNames.TrackClose, leadTools);
        Assert.Contains(KernelMcpToolNames.TemplateList, leadTools);
        Assert.Contains(KernelMcpToolNames.TemplateRead, leadTools);
        Assert.Contains(KernelMcpToolNames.TrackSet, leadTools);
        Assert.DoesNotContain(KernelMcpToolNames.TaskDone, leadTools);
        Assert.DoesNotContain(KernelMcpToolNames.TaskFail, leadTools);

        // Lead calling completion-only tools -> fenced with isError: true!
        var (_, doneByLeadErr) = await CallToolAsync(cluster, "/mcp/kernel", leadToken, KernelMcpToolNames.TaskDone, new { }, ct);
        Assert.True(doneByLeadErr);
        var (_, failByLeadErr) = await CallToolAsync(cluster, "/mcp/kernel", leadToken, KernelMcpToolNames.TaskFail, new { reason = "nope" }, ct);
        Assert.True(failByLeadErr);

        // 2. Exercise shared tools on Lead: report_read, report_commit, task_list, track_status
        var (readText, readErr) = await CallToolAsync(cluster, "/mcp/kernel", leadToken, KernelMcpToolNames.ReportRead, new { }, ct);
        Assert.False(readErr);
        using var reportReadDoc = JsonDocument.Parse(readText);
        var anchorElem = reportReadDoc.RootElement.GetProperty("anchor");

        // Prepare subagent hook to pause inside contract attempt so we can exercise BoundAttemptId != null fences!
        var contractSubagentTokenTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSubagentFinishTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        cluster.AgentRuntime.SetTurnHandler(InterceptSubagentTurnAsync);

        async IAsyncEnumerable<UnifiedAgentEvent> InterceptSubagentTurnAsync(
            HarnessLaunchSpec spec,
            TurnInput input,
            [EnumeratorCancellation] CancellationToken innerCt)
        {
            if (input.Prompt.Contains("Execute contract task 'work-1'", StringComparison.Ordinal))
            {
                var subHttp = Assert.IsType<McpServerConfig.HttpMcpServer>(spec.McpServers["perga"]);
                var subToken = subHttp.Headers["Authorization"]["Bearer ".Length..];
                contractSubagentTokenTcs.TrySetResult(subToken);
                await allowSubagentFinishTcs.Task.WaitAsync(innerCt);
            }

            yield return new UnifiedAgentEvent.TextDelta("turn ok");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(20, 10, 0, 100_000));
        }

        // Commit two tasks via report_commit: 'work-1' (ready=true) and 'work-cancel' (ready=false)
        var (commitText, commitErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.ReportCommit,
            new
            {
                anchor = anchorElem,
                summary = "Added contract tasks",
                ops = new object[]
                {
                    new
                    {
                        op = "upsert_block",
                        id = "b_t1",
                        blockKind = "task",
                        payload = new
                        {
                            taskKey = "work-1",
                            kind = "subagent",
                            access = "read_write",
                            ready = true,
                            goal = "Implement feature 1"
                        }
                    },
                    new
                    {
                        op = "upsert_block",
                        id = "b_t2",
                        blockKind = "task",
                        payload = new
                        {
                            taskKey = "work-cancel",
                            kind = "subagent",
                            access = "read_only",
                            ready = false,
                            goal = "Draft task to cancel"
                        }
                    }
                }
            },
            ct);
        Assert.False(commitErr, commitText);

        var (taskListText, taskListErr) = await CallToolAsync(cluster, "/mcp/kernel", leadToken, KernelMcpToolNames.TaskList, new { }, ct);
        Assert.False(taskListErr);
        Assert.Contains("\"access\":\"read_write\"", taskListText, StringComparison.Ordinal);

        var (statusText, statusErr) = await CallToolAsync(cluster, "/mcp/kernel", leadToken, KernelMcpToolNames.TrackStatus, new { }, ct);
        Assert.False(statusErr);
        Assert.Contains(trackAId, statusText, StringComparison.Ordinal);

        // Bound Workers observe sessions and submit their own task results.
        var contractToken = await contractSubagentTokenTcs.Task.WaitAsync(ct);
        var subTools = await ListToolNamesAsync(cluster, "/mcp/kernel", contractToken, ct);
        Assert.Equal(17, subTools.Count);
        Assert.Contains(KernelMcpToolNames.ProviderList, subTools);
        Assert.Contains(KernelMcpToolNames.DataPatch, subTools);
        Assert.Contains(KernelMcpToolNames.TaskDone, subTools);
        Assert.Contains(KernelMcpToolNames.TaskFail, subTools);
        Assert.DoesNotContain(KernelMcpToolNames.TaskAccept, subTools);
        Assert.DoesNotContain(KernelMcpToolNames.TaskCancel, subTools);
        Assert.Contains(KernelMcpToolNames.SessionList, subTools);
        Assert.Contains(KernelMcpToolNames.SessionRead, subTools);
        Assert.DoesNotContain(KernelMcpToolNames.TrackClose, subTools);

        // Bound Workers cannot invoke governance even when bypassing tools/list.
        foreach (var (govTool, govArgs) in new (string, object)[]
                 {
                     (KernelMcpToolNames.TaskCancel, new { taskKey = "work-cancel" }),
                     (KernelMcpToolNames.TaskAccept, new { taskKey = "work-1" }),
                     (KernelMcpToolNames.TaskReject, new { taskKey = "work-1", note = "bad" }),
                     (KernelMcpToolNames.MailSend, new { toTrackId = trackBId, summary = "hi", body = "hi" }),
                     (KernelMcpToolNames.TrackRename, new { title = "Hijack" }),
                     (KernelMcpToolNames.TrackClose, new { }),
                     (KernelMcpToolNames.TemplateList, new { }),
                     (KernelMcpToolNames.TemplateRead, new { }),
                     (KernelMcpToolNames.TrackSet, new { templateId = "dev" })
                 })
        {
            var (_, isFenceErr) = await CallToolAsync(cluster, "/mcp/kernel", contractToken, govTool, govArgs, ct);
            Assert.True(isFenceErr, $"Expected governance tool '{govTool}' to be fenced for Contract Subagent.");
        }

        // Contract Subagent editing a 'task' block via report_commit -> fenced with isError: true!
        var (subReadText, _) = await CallToolAsync(cluster, "/mcp/kernel", contractToken, KernelMcpToolNames.ReportRead, new { }, ct);
        using var subReadDoc = JsonDocument.Parse(subReadText);
        var subAnchor = subReadDoc.RootElement.GetProperty("anchor");

        var (_, taskBlockEditErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            contractToken,
            KernelMcpToolNames.ReportCommit,
            new
            {
                anchor = subAnchor,
                ops = new object[]
                {
                    new { op = "delete_block", id = "b_t2" }
                }
            },
            ct);
        Assert.True(taskBlockEditErr);

        // Contract Subagent editing a 'prose' block -> allowed!
        var (_, proseEditErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            contractToken,
            KernelMcpToolNames.ReportCommit,
            new
            {
                anchor = subAnchor,
                ops = new object[]
                {
                    new { op = "upsert_block", id = "b_sub_note", blockKind = "prose", markdown = "## Worker Progress\n\nAll checks green.\n" }
                }
            },
            ct);
        Assert.False(proseEditErr);

        // Contract Subagent submits work via task_done!
        var (doneText, doneErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            contractToken,
            KernelMcpToolNames.TaskDone,
            new { result = new { summary = "Implemented feature 1" } },
            ct);
        Assert.False(doneErr, doneText);
        using var revokedDone = await cluster.PostMcpAsync("/mcp/kernel", contractToken,
            new { jsonrpc = "2.0", id = 91, method = "tools/list" }, ct: ct);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedDone.StatusCode);
        allowSubagentFinishTcs.TrySetResult();

        await kernel.WaitForTrackIdleAsync(trackAId, ct);

        // Also test task_fail on a dedicated bound attempt session!
        using var failSubReadDoc = JsonDocument.Parse(
            (await CallToolAsync(cluster, "/mcp/kernel", leadToken, KernelMcpToolNames.ReportRead, new { }, ct)).Text);
        var failTaskTriggeredTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailSubagentTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        cluster.AgentRuntime.SetTurnHandler(InterceptFailSubagentAsync);

        async IAsyncEnumerable<UnifiedAgentEvent> InterceptFailSubagentAsync(
            HarnessLaunchSpec spec,
            TurnInput input,
            [EnumeratorCancellation] CancellationToken innerCt)
        {
            if (input.Prompt.Contains("Execute contract task 'work-fail'", StringComparison.Ordinal))
            {
                var subHttp = Assert.IsType<McpServerConfig.HttpMcpServer>(spec.McpServers["perga"]);
                failTaskTriggeredTcs.TrySetResult(subHttp.Headers["Authorization"]["Bearer ".Length..]);
                await releaseFailSubagentTcs.Task.WaitAsync(innerCt);
            }

            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }

        await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.ReportCommit,
            new
            {
                anchor = failSubReadDoc.RootElement.GetProperty("anchor"),
                ops = new object[]
                {
                    new
                    {
                        op = "upsert_block",
                        id = "b_tfail",
                        blockKind = "task",
                        payload = new { taskKey = "work-fail", kind = "subagent", access = "read_only", ready = true, goal = "Will fail" }
                    }
                }
            },
            ct);

        var failSubToken = await failTaskTriggeredTcs.Task.WaitAsync(ct);
        var (failResText, failResErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            failSubToken,
            KernelMcpToolNames.TaskFail,
            new { reason = "Missing upstream credential" },
            ct);
        Assert.False(failResErr, failResText);
        using var revokedFail = await cluster.PostMcpAsync("/mcp/kernel", failSubToken,
            new { jsonrpc = "2.0", id = 92, method = "tools/list" }, ct: ct);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedFail.StatusCode);
        releaseFailSubagentTcs.TrySetResult();
        await kernel.WaitForTrackIdleAsync(trackAId, ct);

        // 4. Exercise remaining shared & governance tools on Lead:
        //    user_ask, terminal_open/read/input, mail_send/ls/cat,
        //    task_accept, task_reject, task_cancel, track_rename, track_close!

        // user_ask (suspends until /asks/{askId}/answer)
        var userAskCallTask = CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.UserAsk,
            new { questions = new[] { new { question = "Proceed with deploy?", options = new[] { "Yes", "No" } } } },
            ct);

        AskDto? pendingAsk = null;
        for (var i = 0; i < 40 && pendingAsk is null; i++)
        {
            var s = await kernel.GetTrackAggregateAsync(trackAId, ct);
            pendingAsk = s.OpenAsks.FirstOrDefault();
            if (pendingAsk is null)
            {
                await Task.Delay(25, ct);
            }
        }

        Assert.NotNull(pendingAsk);
        using var ansResp = await client.PostAsJsonAsync(
            $"/api/v1/sessions/{pendingAsk!.SessionId}/interactions/{pendingAsk.Id}/respond",
            new { answers = new { answer = "Yes" } },
            ct);
        ansResp.EnsureSuccessStatusCode();
        var (askOutText, askOutErr) = await userAskCallTask;
        Assert.False(askOutErr, askOutText);
        Assert.Contains("Yes", askOutText, StringComparison.Ordinal);

        // terminal_open + terminal_input + terminal_read
        var (termOpenText, termOpenErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.TerminalOpen,
            new { title = "MCP Term", program = "/bin/sh", idempotencyKey = "mcp-term-1" },
            ct);
        Assert.False(termOpenErr, termOpenText);
        using var termOpenDoc = JsonDocument.Parse(termOpenText);
        var mcpTermId = termOpenDoc.RootElement.GetProperty("id").GetString()!;

        var (_, termInErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.TerminalInput,
            new { terminalId = mcpTermId, kind = "submit", payload = new { text = "printf 'MCP_PTY_777\\n'" } },
            ct);
        Assert.False(termInErr);

        var (termReadText, termReadErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.TerminalRead,
            new { terminalId = mcpTermId, waitFor = "elapsed", waitMs = 200 },
            ct);
        Assert.False(termReadErr, termReadText);
        Assert.Contains("MCP_PTY_777", termReadText, StringComparison.Ordinal);

        // mail_send (Track A -> Track B) + mail_list & mail_read on Track B!
        var (mailSendText, mailSendErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.MailSend,
            new { toTrackId = trackBId, summary = "API Spec Ready", body = "All 24 routes are live." },
            ct);
        Assert.False(mailSendErr, mailSendText);
        using var sentMailDoc = JsonDocument.Parse(mailSendText);
        var mailId = sentMailDoc.RootElement.GetProperty("id").GetString()!;

        var (mailListText, mailListErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            peerLeadToken,
            KernelMcpToolNames.MailList,
            new { unreadOnly = true },
            ct);
        Assert.False(mailListErr);
        Assert.Contains(mailId, mailListText, StringComparison.Ordinal);

        var (mailReadText, mailReadErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            peerLeadToken,
            KernelMcpToolNames.MailRead,
            new { mailId },
            ct);
        Assert.False(mailReadErr);
        Assert.Contains("All 24 routes are live.", mailReadText, StringComparison.Ordinal);

        var (replyText, replyError) = await CallToolAsync(cluster, "/mcp/kernel", peerLeadToken,
            KernelMcpToolNames.MailSend, new { replyToMailId = mailId, summary = "Received", body = "Review underway" }, ct);
        Assert.False(replyError, replyText);
        var reply = Assert.Single(await kernel.ListMailsAsync(trackAId, ct: ct));
        Assert.Equal(mailId, reply.ReplyToMailId);
        Assert.Equal(trackBId, reply.FromTrackId);

        var (_, foreignParentError) = await CallToolAsync(cluster, "/mcp/kernel", leadToken,
            KernelMcpToolNames.MailSend, new { replyToMailId = mailId, summary = "Wrong inbox", body = "test" }, ct);
        Assert.True(foreignParentError);
        var (_, twoAddressesError) = await CallToolAsync(cluster, "/mcp/kernel", peerLeadToken,
            KernelMcpToolNames.MailSend, new { toTrackId = trackAId, replyToMailId = mailId, summary = "Both", body = "test" }, ct);
        Assert.True(twoAddressesError);
        Assert.Single(await kernel.ListMailsAsync(trackAId, ct: ct));

        // task_accept, task_reject, task_cancel, track_rename, track_close!
        var (accText, accErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.TaskAccept,
            new { taskKey = "work-1", note = "Clean implementation" },
            ct);
        Assert.False(accErr, accText);

        var (rejText, rejErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.TaskReject,
            new { taskKey = "work-fail", note = "Add fallback logic", reQueue = false },
            ct);
        Assert.False(rejErr, rejText);

        var (canText, canErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.TaskCancel,
            new { taskKey = "work-cancel", reason = "Deferred to Stage 3" },
            ct);
        Assert.False(canErr, canText);

        var (renText, renErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.TrackRename,
            new { title = "Primary Track A (Completed)" },
            ct);
        Assert.False(renErr, renText);
        Assert.Contains("Primary Track A (Completed)", renText, StringComparison.Ordinal);

        var (clsText, clsErr) = await CallToolAsync(
            cluster,
            "/mcp/kernel",
            leadToken,
            KernelMcpToolNames.TrackClose,
            new { },
            ct);
        Assert.False(clsErr, clsText);
    }

    [Fact]
    public async Task McpVcs_All9Tools_AgainstRealJujutsuRepo_And_GitHubForge_WorkEndToEnd()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        // Initialize a real colocated jj repository with a 'main' bookmark + working copy commit!
        var jjRepo = Path.Combine(cluster.DataDir, "mcp-jj-repo");
        Directory.CreateDirectory(jjRepo);
        await ApiV1RestIntegrationTests.RunCliCheckedAsync("jj", ["git", "init", "--colocate"], jjRepo);
        await File.WriteAllTextAsync(Path.Combine(jjRepo, "service.cs"), "public class V1 {}\n", ct);
        await ApiV1RestIntegrationTests.RunCliCheckedAsync("jj", ["describe", "-m", "base main"], jjRepo);
        await ApiV1RestIntegrationTests.RunCliCheckedAsync("jj", ["bookmark", "create", "main", "-r", "@"], jjRepo);
        await ApiV1RestIntegrationTests.RunCliCheckedAsync("jj", ["new", "main", "-m", "feature work"], jjRepo);
        await File.WriteAllTextAsync(Path.Combine(jjRepo, "feature.txt"), "feature v1\n", ct);

        using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "VCS MCP", defaultCwd = jjRepo }, ct);
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, ct))!;

        using var trackResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new { areaId = area.Id, title = "VCS MCP Track", cwd = jjRepo },
            ct);
        using var snap = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync(ct));
        var trackId = snap.RootElement.GetProperty("track").GetProperty("id").GetString()!;
        var leadId = snap.RootElement.GetProperty("track").GetProperty("leadSessionId").GetString()!;

        var bridge = cluster.Services.GetRequiredService<IMcpSessionBridge>();
        var token = bridge.RegisterSession(trackId, leadId, isLead: true, null, null, []).Principal.Token;

        // Verify tools/list exposes all 9 vcs_* tools!
        var vcsTools = await ListToolNamesAsync(cluster, "/mcp/vcs", token, ct);
        Assert.Equal(9, vcsTools.Count);
        foreach (var expected in VcsMcpToolNames.All)
        {
            Assert.Contains(expected, vcsTools);
        }

        // 1. status (captures initial OpId!)
        var (status1Text, status1Err) = await CallToolAsync(cluster, "/mcp/vcs", token, VcsMcpToolNames.Status, new { }, ct);
        Assert.False(status1Err, status1Text);
        using var status1Doc = JsonDocument.Parse(status1Text);
        Assert.Equal("jj_colocated", status1Doc.RootElement.GetProperty("kind").GetString());
        var checkpointOpId = status1Doc.RootElement.GetProperty("opId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(checkpointOpId));

        // 2. diff
        var (diffText, diffErr) = await CallToolAsync(cluster, "/mcp/vcs", token, VcsMcpToolNames.Diff, new { }, ct);
        Assert.False(diffErr, diffText);
        Assert.Contains("feature.txt", diffText, StringComparison.Ordinal);

        // 3. Mutate working copy & snapshot, then roll back via restore_op (JJ time machine)!
        await File.WriteAllTextAsync(Path.Combine(jjRepo, "accidental.txt"), "oops\n", ct);
        _ = await CallToolAsync(cluster, "/mcp/vcs", token, VcsMcpToolNames.Status, new { }, ct);
        Assert.True(File.Exists(Path.Combine(jjRepo, "accidental.txt")));

        var (restoreText, restoreErr) = await CallToolAsync(
            cluster,
            "/mcp/vcs",
            token,
            VcsMcpToolNames.RestoreOp,
            new { opId = checkpointOpId },
            ct);
        Assert.False(restoreErr, restoreText);
        Assert.False(File.Exists(Path.Combine(jjRepo, "accidental.txt")));

        // 4. rebase_upstream onto 'main'
        var (rebaseText, rebaseErr) = await CallToolAsync(
            cluster,
            "/mcp/vcs",
            token,
            VcsMcpToolNames.RebaseUpstream,
            new { upstreamBranch = "main" },
            ct);
        Assert.False(rebaseErr, rebaseText);

        // 5-9. GitHub Forge tools: publish_pr, pr_checks, ci_evidence, pr_merge, issue_view
        var (prText, prErr) = await CallToolAsync(
            cluster,
            "/mcp/vcs",
            token,
            VcsMcpToolNames.PublishPr,
            new { headBranch = "feat/mcp", baseBranch = "main", title = "Add MCP host", body = "Full Streamable HTTP" },
            ct);
        Assert.False(prErr, prText);
        Assert.Contains("\"number\":42", prText, StringComparison.Ordinal);

        var (checksText, checksErr) = await CallToolAsync(
            cluster,
            "/mcp/vcs",
            token,
            VcsMcpToolNames.PrChecks,
            new { prNumber = 42 },
            ct);
        Assert.False(checksErr, checksText);
        Assert.Contains("\"allGreen\":true", checksText, StringComparison.Ordinal);

        var (ciText, ciErr) = await CallToolAsync(
            cluster,
            "/mcp/vcs",
            token,
            VcsMcpToolNames.CiEvidence,
            new { prNumber = 42, headSha = "abc1234def5678" },
            ct);
        Assert.False(ciErr, ciText);
        Assert.Contains("unexpected console.log", ciText, StringComparison.Ordinal);

        var (mergeText, mergeErr) = await CallToolAsync(
            cluster,
            "/mcp/vcs",
            token,
            VcsMcpToolNames.PrMerge,
            new { prNumber = 42, expectedHeadSha = "abc1234def5678" },
            ct);
        Assert.False(mergeErr, mergeText);
        Assert.Single(cluster.ForgeService.MergedPrs);

        var (issueText, issueErr) = await CallToolAsync(
            cluster,
            "/mcp/vcs",
            token,
            VcsMcpToolNames.IssueView,
            new { issueNumber = 7 },
            ct);
        Assert.False(issueErr, issueText);
        Assert.Contains("Issue #7", issueText, StringComparison.Ordinal);
    }

    internal static async Task<IReadOnlyList<string>> ListToolNamesAsync(
        PergaHostTestCluster cluster,
        string mcpPath,
        string token,
        CancellationToken ct)
    {
        using var resp = await cluster.PostMcpAsync(
            mcpPath,
            token,
            new { jsonrpc = "2.0", id = 1, method = "tools/list" },
            ct: ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToArray();
    }

    internal static async Task<(string Text, bool IsError)> CallToolAsync(
        PergaHostTestCluster cluster,
        string mcpPath,
        string token,
        string toolName,
        object arguments,
        CancellationToken ct)
    {
        using var resp = await cluster.PostMcpAsync(
            mcpPath,
            token,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new { name = toolName, arguments }
            },
            ct: ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var res = doc.RootElement.GetProperty("result");
        var isError = res.TryGetProperty("isError", out var errProp) && errProp.GetBoolean();
        var text = res.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        return (text, isError);
    }
}
