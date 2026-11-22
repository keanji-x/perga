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
using Perga.Kernel.Events;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;
using static Perga.Host.IntegrationTests.McpKernelAndVcsIntegrationTests;

namespace Perga.Host.IntegrationTests;

public sealed class UnifiedSessionMcpTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    [Fact]
    public async Task BackgroundRuntimeWritesTranscriptAndGovernanceThroughHost()
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, lead) = await host.CreateTrackAsync();
        var caller = await kernel.OpenMcpCallerAsync(lead, ct);
        var settled = await WaitPage(kernel, lead, page => page.State == "idle" && page.LastTurn?.Status == "completed", ct);
        var store = host.Services.GetRequiredService<ITrackStore>();
        var lastEvent = (await store.ListEventsSinceAsync(0, trackId, ct: ct)).Last().EventId!.Value;

        host.AgentRuntime.EmitRuntimeActivity(lead, new UnifiedAgentEvent.ThinkingDelta("Checking background task"));
        host.AgentRuntime.EmitRuntimeActivity(lead, new UnifiedAgentEvent.ToolCallStarted("background", "perga:flow_commit", JsonSerializer.SerializeToElement(new { })));
        var flow = await Success<FlowCommitResultDto>(host, caller.Token, KernelMcpToolNames.FlowCommit,
            new { content = """{"rev":0,"nodes":[]}""" }, ct);
        Assert.True(flow.Ok);
        Assert.Equal(1, flow.FlowRev);
        host.AgentRuntime.EmitRuntimeActivity(lead, new UnifiedAgentEvent.ToolCallCompleted("background", "Committed", false));
        host.AgentRuntime.EmitRuntimeActivity(lead, new UnifiedAgentEvent.TextDelta("Follow-up complete"));

        var page = await WaitPage(kernel, lead, page => page.Items.Count(item => item.TurnId is null) == 4, ct);
        Assert.Equal("idle", page.State);
        Assert.Null(page.ActiveTurn);
        Assert.Equal(settled.LastTurn!.Id, page.LastTurn!.Id);
        Assert.Equal("completed", page.LastTurn.Status);
        var events = await store.ListEventsSinceAsync(lastEvent, trackId, ct: ct);
        Assert.Contains(events, evt => evt.Kind == KernelEventKinds.SessionUpdated && evt.Payload.GetProperty("id").GetString() == lead);
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync(ct);
        Assert.Equal(["thinking", "tool_call", "tool_result", "assistant_text"], await db.QueryAsync<string>(
            "SELECT kind FROM session_items WHERE session_id = @lead AND turn_id IS NULL ORDER BY id", new { lead }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM session_turns WHERE session_id = @lead", new { lead }));

        var child = await Success<SessionCreationReceipt>(host, caller.Token, KernelMcpToolNames.SessionCreate,
            new { title = "Continued", requestKey = "post-turn" }, ct);
        var sent = await Success<SessionMessageReceipt>(host, caller.Token, KernelMcpToolNames.SessionSend,
            new { sessionId = child.Session.Id, content = "Proceed", requestKey = "background-send" }, ct);
        Assert.NotNull(sent.TurnId);
        await Error(host, caller.Token, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Session.Id, action = "stop", turnId = "retired-turn" }, "conflict", ct);

        var childSettled = await WaitPage(kernel, child.Session.Id, page => page.ActiveTurn is null && page.LastTurn?.Status == "completed", ct);
        await Error(host, caller.Token, KernelMcpToolNames.SessionSend, new
        {
            sessionId = child.Session.Id, content = "Stale steer", requestKey = "stale-steer",
            mode = "steer", turnId = childSettled.LastTurn!.Id
        }, "conflict", ct);
        var nonLead = kernel.RuntimeCaller(child.Session.Id);
        await Error(host, nonLead.Token, KernelMcpToolNames.FlowCommit, new { dryRun = true }, "forbidden", ct);
        await kernel.UpdateSessionAsync(lead, new(Archived: true), Human, ct);
        using var revoked = await host.PostMcpAsync("/mcp/kernel", caller.Token,
            new { jsonrpc = "2.0", id = 79, method = "tools/call", @params = new { name = KernelMcpToolNames.SessionCreate, arguments = new { } } }, ct: ct);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

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
        Assert.Equal(8, tools.Count(t => t.StartsWith("session_", StringComparison.Ordinal)));
        Assert.DoesNotContain("task_dispatch", tools);
        Assert.DoesNotContain(KernelMcpToolNames.SystemUpgradeApply, tools);
        Assert.DoesNotContain("task_accept", tools);
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

    [Fact]
    public async Task CrossProviderChildUsesTargetDefaults()
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, _) = await host.CreateTrackAsync();
        var parent = (await kernel.CreateSessionAsync(trackId,
            new(Provider: "acp:antigravity", Model: "gemini-3.1-pro", ReasoningEffort: "high", PermissionMode: "bypassPermissions"),
            "acp-parent", Human, ct)).Receipt.Session;
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        var token = await StartCaller(host, kernel, parent.Id, ct);

        var child = (await Success<SessionCreationReceipt>(host, token, KernelMcpToolNames.SessionCreate,
            new { provider = "codex" }, ct)).Session;

        Assert.Equal("codex", child.Provider);
        Assert.Null(child.Model);
        Assert.Null(child.ReasoningEffort);
        Assert.Equal("never", child.PermissionMode);

        var inherited = (await Success<SessionCreationReceipt>(host, token, KernelMcpToolNames.SessionCreate,
            new { provider = "agy" }, ct)).Session;
        Assert.Equal((parent.Provider, parent.Model, parent.ReasoningEffort, parent.PermissionMode),
            (inherited.Provider, inherited.Model, inherited.ReasoningEffort, inherited.PermissionMode));
        var switched = await Success<AgentSessionDto>(host, token, KernelMcpToolNames.SessionUpdate,
            new { sessionId = inherited.Id, expectedConfigVersion = inherited.ConfigVersion, provider = "codex" }, ct);
        Assert.Equal((child.Provider, child.Model, child.ReasoningEffort, child.PermissionMode),
            (switched.Provider, switched.Model, switched.ReasoningEffort, switched.PermissionMode));
        Assert.Equal(switched, (await kernel.GetSessionItemsAsync(switched.Id, new(), Human, ct)).Session);
    }

    [Fact]
    public async Task ProviderDiscoveryFiltersAndReusesBootstrapCache()
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (_, lead) = await host.CreateTrackAsync();
        host.AgentRuntime.ProviderStatuses = LaunchTestCatalog;
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        var token = await StartCaller(host, kernel, lead, ct);
        Assert.Contains(KernelMcpToolNames.ProviderList, await ListToolNamesAsync(host, "/mcp/kernel", token, ct));

        await kernel.GetBootstrapAsync(ct);
        var probes = host.AgentRuntime.ProbeCount;
        var first = await Success<TrackProvidersDto>(host, token, KernelMcpToolNames.ProviderList, new { }, ct);
        Assert.Equal("codex", first.DefaultProvider);
        Assert.Equal(new[] { "codex", "acp:antigravity" }, first.Providers.Select(provider => provider.Provider));
        var all = await Success<TrackProvidersDto>(host, token, KernelMcpToolNames.ProviderList, new { includeUnavailable = true }, ct);
        Assert.Equal("missing binary", Assert.Single(all.Providers, provider => !provider.Available).Reason);
        var filtered = await Success<TrackProvidersDto>(host, token, KernelMcpToolNames.ProviderList, new { provider = "agy" }, ct);
        Assert.Equal("acp:antigravity", Assert.Single(filtered.Providers).Provider);
        var unavailable = await Success<TrackProvidersDto>(host, token, KernelMcpToolNames.ProviderList, new { provider = "opencode" }, ct);
        Assert.False(Assert.Single(unavailable.Providers).Available);
        Assert.Equal(first.ObservedAtMs, filtered.ObservedAtMs);
        Assert.Equal(probes, host.AgentRuntime.ProbeCount);

        await AssertLaunchValidationError(host, token, KernelMcpToolNames.ProviderList, new { provider = "missing" }, "invalid_argument", "codex", ct);
        await Error(host, token, KernelMcpToolNames.ProviderList, new { extra = true }, "invalid_argument", ct);
        var refreshes = host.AgentRuntime.ForceRefreshProbeCount;
        host.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await Success<TrackProvidersDto>(host, token, KernelMcpToolNames.ProviderList, new { refresh = true }, ct);
        Assert.True(refreshed.ObservedAtMs > first.ObservedAtMs);
        Assert.Equal(probes + 1, host.AgentRuntime.ProbeCount);
        Assert.Equal(refreshes + 1, host.AgentRuntime.ForceRefreshProbeCount);
    }

    [Theory]
    [InlineData("provider", "missing", "invalid_argument", "codex")]
    [InlineData("provider", "opencode", "precondition_failed", "missing binary")]
    [InlineData("model", "unknown-model", "invalid_argument", "gpt-5.4")]
    [InlineData("reasoningEffort", "ultra", "invalid_argument", "medium")]
    [InlineData("permissionMode", "full_access", "invalid_argument", "never (Never, default)")]
    public async Task InvalidLaunchSettingsFailBeforeSessionOrTaskWrites(string field, string value, string code, string choice)
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (track, lead) = await host.CreateTrackAsync();
        host.AgentRuntime.ProviderStatuses = LaunchTestCatalog;
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        var token = await StartCaller(host, kernel, lead, ct);
        var child = (await Success<SessionCreationReceipt>(host, token, KernelMcpToolNames.SessionCreate, new { }, ct)).Session;
        var report = await kernel.ReadReportAsync(track, ct: ct);

        var create = new Dictionary<string, object> { [field] = value };
        await AssertLaunchValidationError(host, token, KernelMcpToolNames.SessionCreate, create, code, choice, ct);
        create["prompt"] = "inspect";
        await AssertLaunchValidationError(host, token, KernelMcpToolNames.SessionCreate, create, code, choice, ct);

        var update = new Dictionary<string, object>
        {
            ["sessionId"] = child.Id, ["expectedConfigVersion"] = child.ConfigVersion, [field] = value
        };
        await AssertLaunchValidationError(host, token, KernelMcpToolNames.SessionUpdate, update, code, choice, ct);
        var stored = await kernel.GetTrackAggregateAsync(track, ct);
        Assert.Equal(2, stored.Sessions.Count);
        Assert.Empty(stored.Tasks);
        Assert.Equal(report.DocRev, stored.Report.DocRev);
        Assert.Equal(child, Assert.Single(stored.Sessions, session => session.Id == child.Id));
    }

    private static IReadOnlyList<ProviderStatus> LaunchTestCatalog { get; } =
    [
        new("codex", true, "1", null, ["gpt-5.4"], ModelCatalog:
            [new("gpt-5.4", "GPT-5.4", "", true, "medium", [new("medium", ""), new("high", "")])]),
        new("acp:antigravity", true, "1", null, ["gemini-3.1-pro"]),
        new("acp:opencode", false, null, "missing binary", [])
    ];

    private static async Task AssertLaunchValidationError(PergaHostTestCluster host, string token, string tool,
        object args, string code, string choice, CancellationToken ct)
    {
        var (text, error) = await CallToolAsync(host, "/mcp/kernel", token, tool, args, ct);
        Assert.True(error, text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.Contains(choice, document.RootElement.GetProperty("error").GetString());
        Assert.Contains(KernelMcpToolNames.ProviderList, document.RootElement.GetProperty("nextStep").GetString());
    }

    [Theory]
    [InlineData(KernelMcpToolNames.SessionList, "{\"extra\":true}")]
    [InlineData(KernelMcpToolNames.SessionCreate, "{\"task\":{\"extra\":true}}")]
    [InlineData(KernelMcpToolNames.SessionCreate, "{\"TASK\":{\"extra\":true}}")]
    public async Task SessionToolsRejectPropertiesOutsideTheirSchema(string tool, string arguments)
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (_, lead) = await host.CreateTrackAsync();
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        var token = await StartCaller(host, kernel, lead, deadline.Token);
        await Error(host, token, tool, JsonSerializer.Deserialize<JsonElement>(arguments), "invalid_argument", deadline.Token);
    }

    [Fact]
    public async Task SessionSendHonorsExpectedEpoch()
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (_, lead) = await host.CreateTrackAsync();
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        var token = await StartCaller(host, kernel, lead, ct);
        var child = await Success<SessionCreationReceipt>(host, token, KernelMcpToolNames.SessionCreate,
            new { title = "original", prompt = "work" }, ct);
        var page = await WaitPage(kernel, child.Session.Id, p => p.State == "running", ct);
        var epoch = page.Session.Epoch;
        await Error(host, token, KernelMcpToolNames.SessionSend,
            new { sessionId = child.Session.Id, content = "stale", mode = "queue", requestKey = "stale-epoch", expectedEpoch = epoch + 1 }, "conflict", ct);
        var queued = await Success<SessionMessageReceipt>(host, token, KernelMcpToolNames.SessionSend,
            new { sessionId = child.Session.Id, content = "queued", mode = "queue", requestKey = "good-epoch", expectedEpoch = epoch }, ct);
        Assert.Equal("queued", queued.Delivery);
        var schema = KernelMcpCatalog.GetAllTools().Single(t => t.Name == KernelMcpToolNames.SessionSend).InputSchema;
        Assert.Equal("integer", schema.GetProperty("properties").GetProperty("expectedEpoch").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadObservesErrorsAndControlRecoversBothKindsOfConversation(bool taskBound)
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, lead) = await host.CreateTrackAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        host.AgentRuntime.SetTurnHandler(HoldTurns);
        var token = await StartCaller(host, kernel, lead, ct);
        host.AgentRuntime.SetTurnHandler(Unknown);
        host.AgentRuntime.CompactHandler = _ => Task.FromResult(new NativeCompactResult());
        var worker = taskBound ? await FlowFixture.StartWorkerAsync(kernel, trackId, "inspect", "lose connection", ct) : default;
        var child = taskBound ? worker.Session : (await Success<SessionCreationReceipt>(host, token,
            KernelMcpToolNames.SessionCreate, new { prompt = "lose connection" }, ct)).Session;
        await WaitPage(kernel, child.Id, p => p.State == "unknown", ct);
        var page = await Success<SessionItemsPageDto>(host, token, KernelMcpToolNames.SessionRead,
            new { sessionId = child.Id, waitMs = 20 }, ct);
        Assert.Equal("connection_lost: connection lost", page.LastTurn!.ErrorText);
        Assert.True(page.UnknownDetail!.CanReconnect);
        var recovered = await Success<AgentSessionDto>(host, token, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Id, action = "reconnect", expectedEpoch = page.Session.Epoch }, ct);
        Assert.Equal("idle", recovered.Status);
        Assert.Equal(page.Session.NativeThreadId, recovered.NativeThreadId);
        await Success<AgentSessionDto>(host, token, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Id, action = "compact" }, ct);
        var reset = await Success<AgentSessionDto>(host, token, KernelMcpToolNames.SessionControl,
            new { sessionId = child.Id, action = "reset_context" }, ct);
        Assert.Equal(recovered.Epoch + 1, reset.Epoch);
        if (taskBound)
        {
            var workerToken = kernel.McpSessionBridge.RegisterSession(reset.TrackId, reset.Id, false,
                worker.Task.Id, 1, []).Principal.Token;
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
    public async Task ApprovalNotificationIsTargetedEphemeralAndRevokedResponderLeavesItPending()
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
        // Pause the audit outside the actor while the delegate finishes and then loses its runtime authority.
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
        await kernel.UpdateSessionAsync(parent.Id, new(Archived: true), Human, ct);
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
              before(call) { return call.toolName === "session_create" ? AskHuman("Allow create?") : Allow(); }
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
        var listing = CallToolAsync(host, "/mcp/kernel", token, KernelMcpToolNames.SessionCreate, new { title = "probe" }, ct);
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
              before(call) { return call.toolName === "session_create" && call.arguments?.title === "probe" ? AskHuman("Allow create?") : Allow(); }
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
        var listing = CallToolAsync(host, "/mcp/kernel", token, KernelMcpToolNames.SessionCreate, new { title = "probe" }, ct);
        await WaitPage(kernel, child.Id, p => p.PendingInteractions?.Count == 1, ct);
        var approval = Assert.Single((await kernel.GetTrackStatusAsync(track.Id, ct)).PendingApprovals);
        Assert.Equal("policy", approval.Source);
        Assert.Equal(KernelMcpToolNames.SessionCreate, approval.ToolName);
        Assert.Equal("probe", approval.Arguments!.Value.GetProperty("title").GetString());
        Assert.Equal(archivedParent ? track.LeadSessionId : parent.Id, approval.DelegateSessionId);
        if (!archivedParent)
            Assert.True(Assert.Single((await Success<SessionItemsPageDto>(host, parentToken, KernelMcpToolNames.SessionRead,
                new { sessionId = child.Id }, ct)).PendingInteractions!).CanRespond);
        Assert.True(Assert.Single((await kernel.GetSessionItemsAsync(child.Id, new(), Human, ct)).PendingInteractions!).CanRespond);
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
        await WaitPage(kernel, sessionId, p => p.ActiveTurn is not null && p.Activity is { Sequence: > 0 }, ct);
        return kernel.RuntimeCaller(sessionId).Token;
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
        if (code == "conflict") Assert.Equal(409, document.RootElement.GetProperty("status").GetInt32());
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
