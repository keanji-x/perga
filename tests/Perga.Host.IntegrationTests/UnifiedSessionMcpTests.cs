using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;
using static Perga.Host.IntegrationTests.McpKernelAndVcsIntegrationTests;

namespace Perga.Host.IntegrationTests;

public sealed class UnifiedSessionMcpTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    [Fact]
    public async Task IndependentSessionsSpawnToDepthLimitAndControlTheirChildren()
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (track, lead) = await host.CreateTrackAsync();
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        var leadToken = await StartCaller(host, kernel, lead, ct);
        var parent = (await Success<SessionCreationReceipt>(host, leadToken, KernelMcpToolNames.SessionCreate,
            new { title = "parent" }, ct)).Session;
        Assert.Equal(lead, parent.ParentSessionId);
        Assert.Equal(1, parent.SpawnDepth);
        var parentToken = await StartCaller(host, kernel, parent.Id, ct);
        var child = await Success<SessionCreationReceipt>(host, parentToken, KernelMcpToolNames.SessionCreate,
            new { title = "child", model = "gpt-5.4" }, ct);
        Assert.False(string.IsNullOrWhiteSpace(child.RequestKey));
        Assert.Equal(parent.Id, child.Session.ParentSessionId);
        Assert.Equal(2, child.Session.SpawnDepth);
        var childToken = await StartCaller(host, kernel, child.Session.Id, ct);
        await Error(host, childToken, KernelMcpToolNames.SessionCreate, new { }, "forbidden", ct);
        var tools = await ListToolNamesAsync(host, "/mcp/kernel", parentToken, ct);
        Assert.Equal(8, tools.Count(t => t.StartsWith("perga_session_", StringComparison.Ordinal)));
        Assert.DoesNotContain("perga_task_dispatch", tools);
        Assert.DoesNotContain(KernelMcpToolNames.SystemUpgradeApply, tools);
        Assert.Contains(KernelMcpToolNames.TaskAccept, tools);
        Assert.DoesNotContain(KernelMcpToolNames.TaskDone, tools);
        var listed = await Success<SessionSummaryDto[]>(host, parentToken, KernelMcpToolNames.SessionList,
            new { parentSessionId = parent.Id }, ct);
        Assert.Equal(child.Session.Id, Assert.Single(listed).Session.Id);
        var page = await Success<SessionItemsPageDto>(host, childToken, KernelMcpToolNames.SessionRead,
            new { sessionId = child.Session.Id }, ct);
        Assert.Equal("running", page.State);
        await Error(host, parentToken, KernelMcpToolNames.SessionSend,
            new { sessionId = child.Session.Id, content = "busy", requestKey = "busy" }, "conflict", ct);
        var queued = await Success<SessionMessageReceipt>(host, parentToken, KernelMcpToolNames.SessionSend,
            new { sessionId = child.Session.Id, content = "queued", mode = "queue", requestKey = "queue" }, ct);
        await Success<JsonElement>(host, parentToken, KernelMcpToolNames.SessionWithdraw,
            new { sessionId = child.Session.Id, messageId = queued.MessageId }, ct);
        await Success<AgentSessionDto>(host, parentToken, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Session.Id, action = "stop", turnId = page.ActiveTurn!.Id }, ct);
        await WaitPage(kernel, child.Session.Id, p => p.State == "idle", ct);
        await Success<AgentSessionDto>(host, parentToken, KernelMcpToolNames.SessionUpdate,
            new { sessionId = child.Session.Id, title = "renamed" }, ct);
        Assert.Equal("gpt-5.4", (await kernel.GetSessionItemsAsync(child.Session.Id, new(), new InteropPrincipal.Human(), ct)).Session.Model);
        var updated = await Success<AgentSessionDto>(host, parentToken, KernelMcpToolNames.SessionUpdate,
            JsonSerializer.SerializeToElement(new { sessionId = child.Session.Id, expectedConfigVersion = 0, model = (string?)null }), ct);
        Assert.Null(updated.Model);
        Assert.Equal(1, updated.ConfigVersion);
        var reset = await Success<AgentSessionDto>(host, parentToken, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Session.Id, action = "reset_context", expectedEpoch = updated.Epoch }, ct);
        Assert.Equal(updated.Epoch + 1, reset.Epoch);
        await Error(host, parentToken, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Session.Id, action = "reconnect" }, "conflict", ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadObservesErrorsAndControlRecoversBothKindsOfConversation(bool taskBound)
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (_, lead) = await host.CreateTrackAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        var token = await StartCaller(host, kernel, lead, ct);
        host.AgentRuntime.SetTurnHandler(Unknown);
        host.AgentRuntime.CompactHandler = _ => Task.FromResult(new NativeCompactResult());
        var child = await Success<SessionCreationReceipt>(host, token, KernelMcpToolNames.SessionCreate,
            new { prompt = "lose connection", task = taskBound ? new { access = "read_only" } : null }, ct);
        await WaitPage(kernel, child.Session.Id, p => p.State == "unknown", ct);
        var page = await Success<SessionItemsPageDto>(host, token, KernelMcpToolNames.SessionRead,
            new { sessionId = child.Session.Id, waitMs = 20 }, ct);
        Assert.Equal("connection_lost: connection lost", page.LastTurn!.ErrorText);
        Assert.True(page.UnknownDetail!.CanReconnect);
        var recovered = await Success<AgentSessionDto>(host, token, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Session.Id, action = "reconnect", expectedEpoch = page.Session.Epoch }, ct);
        Assert.Equal("idle", recovered.Status);
        Assert.Equal(page.Session.NativeThreadId, recovered.NativeThreadId);
        await Success<AgentSessionDto>(host, token, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Session.Id, action = "compact" }, ct);
        var reset = await Success<AgentSessionDto>(host, token, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Session.Id, action = "reset_context" }, ct);
        Assert.Equal(recovered.Epoch + 1, reset.Epoch);
        Assert.Equal(recovered.BoundAttemptId, reset.BoundAttemptId);
        if (taskBound)
        {
            var workerToken = kernel.McpSessionBridge.RegisterSession(reset.TrackId, reset.Id, false,
                reset.BoundTaskId, reset.BoundAttemptId, []).Principal.Token;
            var tools = await ListToolNamesAsync(host, "/mcp/kernel", workerToken, ct);
            Assert.Contains(KernelMcpToolNames.SessionRead, tools);
            Assert.Contains(KernelMcpToolNames.SessionList, tools);
            Assert.Contains(KernelMcpToolNames.TaskDone, tools);
            Assert.DoesNotContain(KernelMcpToolNames.SessionCreate, tools);
            await Error(host, workerToken, KernelMcpToolNames.SessionCreate, new { }, "forbidden", ct);
        }

        static async IAsyncEnumerable<UnifiedAgentEvent> Unknown(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.TextDelta("partial");
            await Task.Yield();
            yield return new UnifiedAgentEvent.TurnError("connection_lost", "connection lost", false);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData("10")]
    [InlineData(0.5)]
    public async Task ReadRejectsInvalidWaitInsteadOfClamping(object waitMs)
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (_, lead) = await host.CreateTrackAsync();
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = await StartCaller(host, kernel, lead, deadline.Token);
        await Error(host, token, KernelMcpToolNames.SessionRead, new { sessionId = lead, waitMs }, "invalid_argument", deadline.Token);
    }

    [Theory]
    [InlineData("peer")]
    [InlineData("self")]
    public async Task OwnershipGuardRejectsPeerWritesAndSelfDestruction(string targetKind)
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (track, _) = await host.CreateTrackAsync();
        var parent = (await kernel.CreateSessionAsync(track, new(Title: "parent"), "parent", Human, default)).Receipt.Session;
        var peer = (await kernel.CreateSessionAsync(track, new(Title: "peer"), "peer", Human, default)).Receipt.Session;
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = await StartCaller(host, kernel, parent.Id, deadline.Token);
        var target = targetKind == "self" ? parent : peer;
        await Error(host, token, KernelMcpToolNames.SessionUpdate, new { sessionId = target.Id, archived = true }, "forbidden", deadline.Token);
        await Error(host, token, KernelMcpToolNames.SessionControl,
            new { sessionId = target.Id, action = "reset_context" }, "forbidden", deadline.Token);
        Assert.Null((await kernel.GetSessionItemsAsync(target.Id, new(), new InteropPrincipal.Human(), default)).Session.ArchivedAtMs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AskTargetsParentAndHumanCanAlwaysAnswer(bool humanAnswers)
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (track, lead) = await host.CreateTrackAsync();
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var token = await StartCaller(host, kernel, lead, ct);
        var child = (await Success<SessionCreationReceipt>(host, token, KernelMcpToolNames.SessionCreate, new { }, ct)).Session;
        var childToken = await StartCaller(host, kernel, child.Id, ct);
        var asking = CallToolAsync(host, "/mcp/kernel", childToken, KernelMcpToolNames.UserAsk,
            new { questions = new[] { new { question = "Choose", options = new[] { "A", "B" } } } }, ct);
        AskDto? ask = null;
        while (ask is null)
        {
            ask = (await kernel.GetTrackStatusAsync(track, ct)).OpenAsks.FirstOrDefault();
            if (ask is null) await Task.Delay(5, ct);
        }
        Assert.Equal(lead, ask.TargetSessionId);
        Assert.True(Assert.Single((await Success<SessionItemsPageDto>(host, token, KernelMcpToolNames.SessionRead,
            new { sessionId = child.Id }, ct)).PendingInteractions!).CanRespond);
        if (humanAnswers)
            await kernel.RespondInteractionAsync(child.Id, ask.Id, new(Answers: JsonSerializer.SerializeToElement(new[] { "A" })), Human, ct);
        else
            await Success<SessionInteractionResult>(host, token, KernelMcpToolNames.SessionRespond,
                new { sessionId = child.Id, interactionId = ask.Id, answers = new[] { "A" } }, ct);
        Assert.False((await asking).IsError);
        Assert.Empty((await kernel.GetSessionItemsAsync(child.Id, new(), new InteropPrincipal.Human(), ct)).PendingInteractions!);
    }

    [Fact]
    public async Task ApprovalNotificationIsTargetedEphemeralAndAuditAbortLeavesItPending()
    {
        AnswerPublicationStoreProxy proxy = null!;
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
        {
            services.RemoveAll<ITrackStore>();
            services.AddSingleton<ITrackStore>(sp =>
            {
                var wrapped = DispatchProxy.Create<ITrackStore, AnswerPublicationStoreProxy>();
                proxy = (AnswerPublicationStoreProxy)wrapped;
                var sqlite = sp.GetRequiredService<SqliteTrackStore>();
                sqlite.InitializeAsync().GetAwaiter().GetResult();
                proxy.Inner = sqlite;
                return wrapped;
            });
        });
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (track, lead) = await host.CreateTrackAsync();
        var parent = (await kernel.CreateSessionAsync(track, new(Title: "parent"), "parent", Human, default)).Receipt.Session;
        // Establish the ownership through production create, from the running independent parent.
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var token = await StartCaller(host, kernel, parent.Id, ct);
        var child = (await Success<SessionCreationReceipt>(host, token, KernelMcpToolNames.SessionCreate, new { }, ct)).Session;
        var approvalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.ApprovalHandler = () => approvalRelease.TrySetResult();
        host.AgentRuntime.SetTurnHandler((spec, input, turnCt) => spec.StateKey == child.Id
            ? ApprovalTurn(approvalRelease.Task, turnCt) : HoldTurns(spec, input, turnCt));
        await kernel.SendSessionMessageAsync(child.Id, new(Content: "approval", RequestKey: "approval"), Human, ct);
        var childPage = await WaitPage(kernel, child.Id, p => p.PendingInteractions?.Count == 1, ct);
        var approval = Assert.Single((await kernel.GetTrackStatusAsync(track, ct)).PendingApprovals);
        Assert.Equal(parent.Id, approval.DelegateSessionId);
        Assert.Equal("provider", approval.Source);
        Assert.Equal("shell", approval.ToolName);
        Assert.True(approval.Arguments!.Value.GetRawText().Length <= 4096);
        Assert.Equal("shell", Assert.Single(childPage.PendingInteractions!).ToolName);
        Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, t => t.Spec.StateKey == lead);
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync(ct);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM track_wakes"));
        var parentTurn = (await kernel.GetSessionItemsAsync(parent.Id, new(), new InteropPrincipal.Human(), ct)).ActiveTurn!.Id;
        // Pause the audit outside the actor, then stop the responder before the secondary check.
        var auditing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeAudit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.BeforeNextApprovalAudit = () =>
        {
            auditing.TrySetResult();
            resumeAudit.Task.WaitAsync(ct).GetAwaiter().GetResult();
        };
        var responding = Task.Run(() => CallToolAsync(host, "/mcp/kernel", token, KernelMcpToolNames.SessionRespond,
            new { sessionId = child.Id, interactionId = approval.Id, executionId = approval.ExecutionId, optionId = "approve" }, ct), ct);
        await auditing.Task.WaitAsync(ct);
        await kernel.ControlSessionAsync(parent.Id, new("stop", TurnId: parentTurn), Human, ct);
        // The pending notification starts a fresh parent turn, never the Lead.
        await WaitPage(kernel, parent.Id, p => p.ActiveTurn is { } turn && turn.Id != parentTurn, ct);
        resumeAudit.TrySetResult();
        Assert.True((await responding).IsError);
        Assert.Single((await kernel.GetTrackStatusAsync(track, ct)).PendingApprovals);
        while (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM events WHERE kind='approval.delivery' AND json_extract(payload_json,'$.detail.delivery')='aborted'") == 0)
            await Task.Delay(5, ct);
        Assert.Empty(host.AgentRuntime.ApprovalResponses);
        Assert.Single(host.AgentRuntime.ExecutedTurns, t => t.Spec.StateKey == parent.Id && t.Input.Prompt.Contains(approval.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, t => t.Spec.StateKey == lead);
        await kernel.RespondInteractionAsync(child.Id, approval.Id, new(approval.ExecutionId, "approve"), Human, ct);
        Assert.Empty((await kernel.GetTrackStatusAsync(track, ct)).PendingApprovals);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM track_wakes"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalDecidedDuringDelegateOpenDoesNotStartNotification(bool expire)
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate("""
            export default defineTemplate({id:"slow-approval",title:"Slow approval",
              before(call) { return call.toolName === "perga_session_list" ? AskHuman("Allow list?") : Allow(); }
            });
            """);
        var area = await kernel.CreateAreaAsync(new("Approvals", DefaultCwd: host.DataDir));
        var track = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "slow-approval", Cwd: host.DataDir))).Track;
        await kernel.WaitForTrackIdleAsync(track.Id);
        var lead = Assert.IsType<string>(track.LeadSessionId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        host.AgentRuntime.SetTurnHandler((spec, input, turnCt) => spec.StateKey == lead
            ? CompleteTurn(turnCt) : HoldTurns(spec, input, turnCt));
        var child = (await kernel.CreateSessionAsync(track.Id, new(), "child", Human, ct)).Receipt.Session;
        var token = await StartCaller(host, kernel, child.Id, ct);
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeOpenAsync = async (spec, openCt) =>
        {
            if (spec.StateKey != lead) return;
            opening.TrySetResult();
            await releaseOpen.Task.WaitAsync(openCt);
        };
        var listing = CallToolAsync(host, "/mcp/kernel", token, KernelMcpToolNames.SessionList, new { }, ct);
        try
        {
            await opening.Task.WaitAsync(ct);
            var approval = Assert.Single((await kernel.GetTrackStatusAsync(track.Id, ct)).PendingApprovals);
            Assert.Equal(lead, approval.DelegateSessionId);
            if (expire)
            {
                var turn = (await kernel.GetSessionItemsAsync(child.Id, new(), new InteropPrincipal.Human(), ct)).ActiveTurn!.Id;
                await kernel.ControlSessionAsync(child.Id, new("stop", TurnId: turn), Human, ct);
            }
            else
                await kernel.RespondInteractionAsync(child.Id, approval.Id, new(approval.ExecutionId, "approve"), Human, ct);
            Assert.Equal(expire, (await listing).IsError);
            Assert.Empty((await kernel.GetTrackStatusAsync(track.Id, ct)).PendingApprovals);
        }
        finally
        {
            releaseOpen.TrySetResult();
        }
        if (!expire)
        {
            var turn = (await kernel.GetSessionItemsAsync(child.Id, new(), new InteropPrincipal.Human(), ct)).ActiveTurn!.Id;
            await kernel.ControlSessionAsync(child.Id, new("stop", TurnId: turn), Human, ct);
        }
        await kernel.WaitForTrackIdleAsync(track.Id, ct);
        Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, t => t.Spec.StateKey == lead);
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync(ct);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM session_turns WHERE session_id=@Id", new { Id = lead }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM session_items WHERE session_id=@Id", new { Id = lead }));
        await kernel.SendSessionMessageAsync(lead, new(Content: "normal", RequestKey: "normal"), Human, ct);
        await kernel.WaitForTrackIdleAsync(track.Id, ct);
        var execution = Assert.Single(host.AgentRuntime.ExecutedTurns, t => t.Spec.StateKey == lead);
        Assert.Equal("normal", execution.Input.Prompt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PolicyApprovalUsesActiveParentOrLeadFallback(bool archivedParent, bool expire)
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate("""
            export default defineTemplate({id:"approval-routing",title:"Approval routing",
              before(call) { return call.toolName === "perga_session_list" ? AskHuman("Allow list?") : Allow(); }
            });
            """);
        var area = await kernel.CreateAreaAsync(new("Approvals", DefaultCwd: host.DataDir));
        var track = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "approval-routing", Cwd: host.DataDir))).Track;
        await kernel.WaitForTrackIdleAsync(track.Id);
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var parent = (await kernel.CreateSessionAsync(track.Id, new(), "parent", Human, ct)).Receipt.Session;
        var parentToken = await StartCaller(host, kernel, parent.Id, ct);
        var child = (await Success<SessionCreationReceipt>(host, parentToken, KernelMcpToolNames.SessionCreate, new { }, ct)).Session;
        if (archivedParent)
        {
            var turn = (await kernel.GetSessionItemsAsync(parent.Id, new(), new InteropPrincipal.Human(), ct)).ActiveTurn!.Id;
            await kernel.ControlSessionAsync(parent.Id, new("stop", TurnId: turn), Human, ct);
            await WaitPage(kernel, parent.Id, p => p.State == "idle", ct);
            await kernel.UpdateSessionAsync(parent.Id, new(Archived: true), Human, ct);
        }
        var token = await StartCaller(host, kernel, child.Id, ct);
        var listing = CallToolAsync(host, "/mcp/kernel", token, KernelMcpToolNames.SessionList, new { includeArchived = true }, ct);
        await WaitPage(kernel, child.Id, p => p.PendingInteractions?.Count == 1, ct);
        var approval = Assert.Single((await kernel.GetTrackStatusAsync(track.Id, ct)).PendingApprovals);
        Assert.Equal("policy", approval.Source);
        Assert.Equal(KernelMcpToolNames.SessionList, approval.ToolName);
        Assert.True(approval.Arguments!.Value.GetProperty("includeArchived").GetBoolean());
        Assert.Equal(archivedParent ? track.LeadSessionId : parent.Id, approval.DelegateSessionId);
        Assert.False(Assert.Single((await Success<SessionItemsPageDto>(host, token, KernelMcpToolNames.SessionRead,
            new { sessionId = child.Id }, ct)).PendingInteractions!).CanRespond);
        if (expire)
        {
            var turn = (await kernel.GetSessionItemsAsync(child.Id, new(), new InteropPrincipal.Human(), ct)).ActiveTurn!.Id;
            await kernel.ControlSessionAsync(child.Id, new("stop", TurnId: turn), Human, ct);
            Assert.True((await listing).IsError);
        }
        else
        {
            await kernel.RespondInteractionAsync(child.Id, approval.Id, new(approval.ExecutionId, "approve"), Human, ct);
            Assert.False((await listing).IsError);
        }
        Assert.Empty((await kernel.GetTrackStatusAsync(track.Id, ct)).PendingApprovals);
        if (!archivedParent)
        {
            var turn = (await kernel.GetSessionItemsAsync(parent.Id, new(), new InteropPrincipal.Human(), ct)).ActiveTurn!.Id;
            await kernel.ControlSessionAsync(parent.Id, new("stop", TurnId: turn), Human, ct);
            await WaitPage(kernel, parent.Id, p => p.State == "idle", ct);
            Assert.Single(host.AgentRuntime.ExecutedTurns, t => t.Spec.StateKey == parent.Id);
        }
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync(ct);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM track_wakes"));
    }

    private static async Task<string> StartCaller(PergaHostTestCluster host, IPergaKernel kernel, string sessionId, CancellationToken ct)
    {
        await kernel.SendSessionMessageAsync(sessionId, new(Content: "hold:" + sessionId, RequestKey: Guid.NewGuid().ToString("N")), Human, ct);
        var page = await WaitPage(kernel, sessionId, p => p.ActiveTurn is not null, ct);
        return kernel.McpSessionBridge.RegisterSession(page.Session.TrackId, sessionId, page.Session.IsLead,
            page.Session.BoundTaskId, page.Session.BoundAttemptId, []).Principal.Token;
    }

    private static async Task<SessionItemsPageDto> WaitPage(IPergaKernel kernel, string session, Func<SessionItemsPageDto, bool> predicate, CancellationToken ct)
    {
        while (true)
        {
            var page = await kernel.GetSessionItemsAsync(session, new(), new InteropPrincipal.Human(), ct);
            if (predicate(page)) return page;
            await Task.Delay(5, ct);
        }
    }

    private static async Task<T> Success<T>(PergaHostTestCluster host, string token, string tool, object args, CancellationToken ct)
    {
        var (text, error) = await CallToolAsync(host, "/mcp/kernel", token, tool, args, ct);
        Assert.False(error, text);
        return JsonSerializer.Deserialize<T>(text, PergaJsonDefaults.ApiOptions)!;
    }

    private static async Task Error(PergaHostTestCluster host, string token, string tool, object args, string code, CancellationToken ct)
    {
        var (text, error) = await CallToolAsync(host, "/mcp/kernel", token, tool, args, ct);
        Assert.True(error, text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        if (code == "forbidden") Assert.Equal(403, document.RootElement.GetProperty("status").GetInt32());
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> CompleteTurn([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> HoldTurns(HarnessLaunchSpec spec, TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.TextDelta("running");
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        yield return new UnifiedAgentEvent.TurnInterrupted();
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> ApprovalTurn(Task release, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ApprovalRequested("approval", "shell", JsonSerializer.SerializeToElement(new { command = new string('"', 8000) }));
        await release.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }
}
