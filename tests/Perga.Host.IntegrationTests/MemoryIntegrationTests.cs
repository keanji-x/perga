using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.Mcp;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.MemoryKit;
using Perga.ReportDoc;
using Perga.VcsKit;
using static Perga.Host.IntegrationTests.McpKernelAndVcsIntegrationTests;

namespace Perga.Host.IntegrationTests;

public sealed partial class MemoryIntegrationTests
{
    private const string McpPath = "/mcp/memory";
    private const string MemoryPath = "/api/v1/memory";
    private static InteropPrincipal.Human Owner => new();
    private const string ClearedContext = "<memory_context>\nMemory is now empty (previous entries were removed).\n</memory_context>\n";

    [Fact]
    public async Task AreaPatchPreservesTodayAndRejectsMemoryAndDeletedAreas()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        Assert.NotNull(await kernel.GetDailyTrackAsync(null, ct));
        await EnsureAsync(host, "/global/track", ct);

        using var daily = await host.AuthenticatedClient.PatchAsJsonAsync("/api/v1/areas/sys_daily", new { name = "Renamed Today" }, ct);
        Assert.Equal(HttpStatusCode.OK, daily.StatusCode);
        var patched = await daily.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, ct);
        Assert.Equal("sys_daily", patched?.Id);
        Assert.Equal("Renamed Today", (await store.GetAreaAsync("sys_daily", ct))?.Name);

        var memoryId = MemoryTrackBinding.SystemAreaId;
        var memory = await store.GetAreaAsync(memoryId, ct);
        Assert.NotNull(memory);
        using var memoryPatch = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/areas/{memoryId}", new { name = "Renamed Memory" }, ct);
        Assert.Equal(HttpStatusCode.NotFound, memoryPatch.StatusCode);
        var memoryWrite = await Assert.ThrowsAsync<KernelException>(() => store.UpdateAreaAsync(memory with { Name = "Renamed Memory" }, ct));
        Assert.Equal(KernelErrorCode.NotFound, memoryWrite.Code);
        Assert.Equal(memory.Name, (await store.GetAreaAsync(memoryId, ct))?.Name);

        var area = await kernel.CreateAreaAsync(new("Project"), ct);
        using var delete = await host.AuthenticatedClient.DeleteAsync($"/api/v1/areas/{area.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var deletedPatch = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/areas/{area.Id}", new { name = "Renamed Project" }, ct);
        Assert.Equal(HttpStatusCode.NotFound, deletedPatch.StatusCode);
        Assert.Null(await store.GetAreaAsync(area.Id, ct));
    }

    [Fact]
    public async Task TodayUsesDefaultWorkspaceAndRemainsVisibleBesideHiddenMemoryPlanner()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster(configureHostOptions: options => options.DefaultWorkspaceDriver = DirectWorkspaceDriver.Id);
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (userTrack, _) = await host.CreateTrackAsync();
        var today = await host.AuthenticatedClient.GetFromJsonAsync<DailyTrackDto>("/api/v1/today/daily", PergaJsonDefaults.ApiOptions, ct);
        Assert.NotNull(today);
        var memory = await EnsureAsync(host, "/global/track", ct);

        var dailyTracks = await host.AuthenticatedClient.GetFromJsonAsync<TrackDto[]>("/api/v1/areas/sys_daily/tracks", PergaJsonDefaults.ApiOptions, ct);
        var daily = Assert.Single(dailyTracks!);
        Assert.Equal(today.TrackId, daily.Id);
        Assert.Equal(DirectWorkspaceDriver.Id, daily.WorkspaceDriver);
        var memoryTracks = await host.AuthenticatedClient.GetFromJsonAsync<TrackDto[]>("/api/v1/areas/sys_memory/tracks", PergaJsonDefaults.ApiOptions, ct);
        Assert.Empty(memoryTracks!);
        var overlays = await host.AuthenticatedClient.GetFromJsonAsync<TrackOverlaysResponseDto>("/api/v1/overlays", PergaJsonDefaults.ApiOptions, ct);
        Assert.Equal(new[] { today.TrackId, userTrack }.Order(StringComparer.Ordinal),
            overlays!.Tracks.Select(track => track.TrackId).Order(StringComparer.Ordinal));

        var dailyWorkspace = await kernel.Actors.GetOrActivate(today.TrackId).EnsureMountedAsync(ct);
        Assert.Equal(DirectWorkspaceDriver.Id, dailyWorkspace.DriverId);
        var memoryWorkspace = await kernel.Actors.GetOrActivate(memory.TrackId).EnsureMountedAsync(ct);
        Assert.Equal(VcsWorkspaceDriver.Id, memoryWorkspace.DriverId);
        Assert.True(memoryWorkspace.ManagedEmptyWorkspace);
        Assert.Null(memoryWorkspace.ProcessOverlayLayers);
        var memoryPath = Path.Combine(host.DataDir, "workspaces", memory.TrackId);
        Assert.True(Directory.Exists(memoryPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(memoryPath));
    }

    [Fact]
    public async Task EnabledMemoryPluginAndBuiltInMemoryHaveSeparateMounts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        using var upstream = new HttpClient(new MemoryPluginHandler());
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
            services.AddSingleton(sp =>
            {
                var router = new McpPluginRouter(sp.GetRequiredService<IPergaKernel>(), new(), upstream);
                router.RegisterPlugin(new("memory", "http", "http://memory-plugin.test/mcp"));
                return router;
            }));
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await kernel.PatchTrackAsync(trackId, new(EnabledPlugins: ["memory"]), ct);

        var turn = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Check MCP mounts", ct);
        var builtIn = Assert.IsType<McpServerConfig.HttpMcpServer>(turn.Spec.McpServers["perga_memory"]);
        var plugin = Assert.IsType<McpServerConfig.HttpMcpServer>(turn.Spec.McpServers["memory"]);
        Assert.EndsWith(McpPath, builtIn.Url, StringComparison.Ordinal);
        Assert.EndsWith("/mcp/p/memory", plugin.Url, StringComparison.Ordinal);
        var caller = await kernel.OpenMcpCallerAsync(sessionId, ct);
        Assert.Contains("memory_list", await ListToolNamesAsync(host, McpPath, caller.Token, ct));
        Assert.Equal(["plugin_lookup"], await ListToolNamesAsync(host, "/mcp/p/memory", caller.Token, ct));
    }

    private sealed class MemoryPluginHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var rpc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var result = rpc.RootElement.GetProperty("method").GetString() switch
            {
                "tools/list" => (object)new { tools = new[] { new { name = "plugin_lookup", inputSchema = new { type = "object" } } } },
                _ => new { },
            };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { result }) };
        }
    }

    [Fact]
    public async Task MemoryPlannerRejectsMcpAndHttpCloseAndRemainsWritable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var planner = await kernel.GetOrEnsureMemoryTrackAsync(null, ct);
        var lead = await kernel.OpenMcpCallerAsync(planner.LeadSessionId, ct);

        var close = await CallToolAsync(host, "/mcp/kernel", lead.Token, KernelMcpToolNames.TrackClose, new { }, ct);
        Assert.True(close.IsError);
        Assert.Contains("Memory Planner Tracks cannot be closed.", close.Text);
        using var patch = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{planner.TrackId}", new { closed = true }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal(planner, await kernel.GetOrEnsureMemoryTrackAsync(null, ct));
        Assert.Null((await kernel.GetTrackAggregateAsync(planner.TrackId, ct)).Track.ClosedAtMs);
        await SuccessfulCallAsync(host, lead, "memory_put", Entry("still-writable"), ct);
        Assert.Equal("global/still-writable", Assert.Single((await ReadTrackAsync(host, planner, ct)).GetProperty("track").GetProperty("data")
            .GetProperty("entries").EnumerateArray()).GetProperty("ref").GetString());
    }

    [Fact]
    public async Task TrackReportAndHeaderProjectLiveMarkdownWithoutPersistingEntries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster(configureHostOptions: options => options.TemplatesDirectory = "");
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new("Project"), ct);
        var global = await EnsureAsync(host, "/global/track", ct);
        var local = await EnsureAsync(host, $"/areas/{area.Id}/track", ct);
        var globalTrack = (await ReadTrackAsync(host, global, ct)).GetProperty("track");
        Assert.Equal("memory", globalTrack.GetProperty("templateId").GetString());
        Assert.Equal("0", Metric(globalTrack, "Entries"));
        var initial = await ReadTrackAsync(host, local, ct);
        Assert.Equal("memory", initial.GetProperty("track").GetProperty("templateId").GetString());
        Assert.Equal([("./widgets/entries.html", "pinned"), ("./widgets/entries.html", "topics"), ("builtin:flow", "flow")],
            initial.GetProperty("report").GetProperty("blocks").EnumerateArray()
            .Where(block => block.GetProperty("kind").GetString() == "widget")
            .Select(block => block.GetProperty("payload"))
            .Select(payload => (payload.GetProperty("use").GetString(), payload.GetProperty("view").GetString())));
        Assert.Equal("0 B / 1.2 KB", Metric(initial.GetProperty("track"), "Pinned"));
        Assert.True(!initial.GetProperty("track").GetProperty("templateHeaderView").TryGetProperty("rules", out var rules)
            || rules.ValueKind == JsonValueKind.Null || rules.GetArrayLength() == 0);

        var directory = Directory.CreateDirectory(Path.Combine(host.DataDir, "memory", "global"));
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "manual.md"), "# Shared note\n\nFrom disk.", ct);
        Assert.Equal("# Shared note\n\nFrom disk.", (await ReadTrackAsync(host, global, ct)).GetProperty("track").GetProperty("data")
            .GetProperty("entries")[0].GetProperty("body").GetString());
        var globalViews = await host.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{global.TrackId}/views", ct);
        Assert.Contains("perga:ready", globalViews.GetProperty("widgets").GetProperty("./widgets/entries.html").GetString());
        Assert.Empty(globalViews.GetProperty("diagnostics").EnumerateArray());
        var manualNote = globalViews.GetProperty("views").GetProperty("topics").GetProperty("entries")[0];
        Assert.Equal("global/manual", manualNote.GetProperty("ref").GetString());
        Assert.True(manualNote.GetProperty("repeatedSummary").GetBoolean());

        var lead = await kernel.OpenMcpCallerAsync(local.LeadSessionId, ct);
        var put = await SuccessfulCallAsync(host, lead, "memory_put", Entry("local", "Use the runbook.", pinned: true), ct);
        var track = (await ReadTrackAsync(host, local, ct)).GetProperty("track");
        var data = track.GetProperty("data");
        Assert.Equal(area.Id, data.GetProperty("areaId").GetString());
        Assert.Equal("area/local", Assert.Single(data.GetProperty("entries").EnumerateArray()).GetProperty("ref").GetString());
        Assert.Equal("1", Metric(track, "Global Inherited"));
        Assert.Equal("1", Metric(track, "Entries"));
        Assert.Equal($"{data.GetProperty("pinnedUsedBytes").GetInt32()} B / 1.2 KB", Metric(track, "Pinned"));
        var store = host.Services.GetRequiredService<ITrackReadStore>();
        Assert.Empty(Assert.IsType<TrackDto>(await store.GetTrackAsync(local.TrackId, ct)).Data.EnumerateObject());

        var uri = $"/api/v1/tracks/{local.TrackId}";
        var views = await host.AuthenticatedClient.GetFromJsonAsync<JsonElement>(uri + "/views", ct);
        Assert.Equal("Use the runbook.", views.GetProperty("views").GetProperty("pinned").GetProperty("entries")[0].GetProperty("body").GetString());
        var patched = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(
            kernel, lead, KernelMcpToolNames.DataPatch, JsonSerializer.SerializeToElement(new { patch = new { entries = Array.Empty<string>() } }), ct));
        Assert.Equal(KernelErrorCode.Forbidden, patched.Code);
        var detached = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(
            kernel, lead, KernelMcpToolNames.TrackSet, JsonSerializer.SerializeToElement(new { clear = true }), ct));
        Assert.Equal(KernelErrorCode.Forbidden, detached.Code);
        using var clear = await host.AuthenticatedClient.DeleteAsync(uri + "/template", ct);
        Assert.Equal(HttpStatusCode.Forbidden, clear.StatusCode);

        await SuccessfulCallAsync(host, lead, "memory_delete", new { key = "local", expectedEtag = put.GetProperty("etag").GetString() }, ct);
        var after = (await ReadTrackAsync(host, local, ct)).GetProperty("track");
        Assert.Equal("0", Metric(after, "Entries"));
        Assert.Empty(after.GetProperty("data").GetProperty("entries").EnumerateArray());
        views = await host.AuthenticatedClient.GetFromJsonAsync<JsonElement>(uri + "/views", ct);
        Assert.Empty(views.GetProperty("views").GetProperty("pinned").GetProperty("entries").EnumerateArray());

        foreach (var binding in new[] { global, local })
        {
            var persisted = Assert.IsType<TrackDto>(await store.GetTrackAsync(binding.TrackId, ct));
            Assert.Empty(persisted.Data.EnumerateObject());
            Assert.Equal(0, persisted.DataRev);
        }

        using var oldBrowse = await host.AuthenticatedClient.GetAsync(MemoryPath + "/global", ct);
        Assert.Equal(HttpStatusCode.NotFound, oldBrowse.StatusCode);

        static string? Metric(JsonElement track, string label) =>
            track.GetProperty("templateHeaderView").GetProperty("metrics").EnumerateArray()
                .Single(metric => metric.GetProperty("label").GetString() == label).GetProperty("value").GetString();
    }

    [Fact]
    public async Task MemoryWidgetViewsPreserveAllPinnedAndTopicEntriesAndFullBodies()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster(configureHostOptions: options => options.TemplatesDirectory = "");
        var planner = await EnsureAsync(host, "/global/track", ct);
        var directory = Directory.CreateDirectory(Path.Combine(host.DataDir, "memory", "global"));
        var longBody = new string('λ', 3100) + "\n\nThe end of the full note.";
        foreach (var (prefix, pinned) in new[] { ("pinned", "true"), ("topic", "false") })
        {
            // Hand-written files are readable even beyond the Planner's pinned write budget.
            for (var i = 0; i <= 100; i++)
            {
                var body = i == 100 ? longBody : $"Short note {i}.";
                var content = $"---\ntitle: \"{prefix} {i}\"\nsummary: \"Separate summary\"\npinned: {pinned}\ntags: [\"checklist\"]\nsources: [\"Team handbook\"]\n---\n{body}";
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, $"{prefix}-{i}.md"), content, ct);
            }
        }

        var projection = await host.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{planner.TrackId}/views", ct);
        Assert.Empty(projection.GetProperty("diagnostics").EnumerateArray());
        foreach (var (kind, prefix) in new[] { ("pinned", "pinned"), ("topics", "topic") })
        {
            var view = projection.GetProperty("views").GetProperty(kind);
            Assert.Equal(kind, view.GetProperty("kind").GetString());
            var entries = view.GetProperty("entries");
            Assert.Equal(101, entries.GetArrayLength());
            Assert.Equal(Enumerable.Range(0, 101).Select(i => $"global/{prefix}-{i}").Order(),
                entries.EnumerateArray().Select(entry => entry.GetProperty("ref").GetString()).Order());
            var longEntry = Assert.Single(entries.EnumerateArray(), entry => entry.GetProperty("ref").GetString() == $"global/{prefix}-100");
            Assert.Equal(longBody, longEntry.GetProperty("body").GetString());
            Assert.Equal($"{prefix} 100", longEntry.GetProperty("title").GetString());
            Assert.Equal("Separate summary", longEntry.GetProperty("summary").GetString());
            Assert.Equal("checklist", longEntry.GetProperty("tags")[0].GetString());
            Assert.Equal("Team handbook", longEntry.GetProperty("sources")[0].GetString());
            Assert.True(longEntry.GetProperty("updatedAtMs").GetInt64() > 0);
            Assert.False(longEntry.GetProperty("repeatedSummary").GetBoolean());
        }
    }

    [Fact]
    public async Task EnsureBindsAnExistingMemoryPlannerAndPreservesItsReport()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new("Project"));
        var source = await kernel.CreateTrackAsync(new(area.Id));
        var trackId = $"trk_{Guid.NewGuid():N}";
        var leadId = $"ses_{Guid.NewGuid():N}";
        var workspace = Directory.CreateDirectory(Path.Combine(host.DataDir, "workspaces", trackId));
        var legacyTrack = source.Track with
        {
            Id = trackId,
            AreaId = MemoryTrackBinding.SystemAreaId,
            LeadSessionId = leadId,
            Cwd = workspace.FullName,
            WorkspaceDriver = VcsWorkspaceDriver.Id
        };
        var lead = source.Sessions.Single() with { Id = leadId, TrackId = trackId };
        var oldReport = new ReportDocEngine().Parse("# Existing report\n\nKeep this block.", docRev: 2);
        var systemArea = area with { Id = legacyTrack.AreaId, Name = "Memory" };
        var store = host.Services.GetRequiredService<ITrackStore>();
        await store.CreateTrackAsync(new(legacyTrack, oldReport, null, lead, [], null, null, [], SystemArea: systemArea)
        {
            MemoryBinding = new("global", null, legacyTrack.Id)
        }, null, default);

        var binding = await EnsureAsync(host, "/global/track", default);
        Assert.Equal(legacyTrack.Id, binding.TrackId);
        Assert.Equal(binding, await EnsureAsync(host, "/global/track", default));
        var snapshot = await kernel.GetTrackAggregateAsync(binding.TrackId);
        Assert.Equal("memory", snapshot.Track.TemplateId);
        Assert.Equal(3, snapshot.Report.DocRev);
        Assert.Equal(3, snapshot.Report.Blocks.Count(block => block.Kind == ReportBlockKind.Widget));
        Assert.Equal(oldReport.Blocks, snapshot.Report.Blocks.TakeLast(oldReport.Blocks.Count));
    }

    [Fact]
    public async Task McpExposesRolesAndCommitsOnlyToTheBoundScope()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var userTrack = (await kernel.GetTrackAggregateAsync(trackId, ct)).Track;
        var lead = await kernel.OpenMcpCallerAsync(sessionId, ct);
        var global = await kernel.GetOrEnsureMemoryTrackAsync(null, ct);
        var area = await kernel.GetOrEnsureMemoryTrackAsync(userTrack.AreaId, ct);
        var globalLead = await kernel.OpenMcpCallerAsync(global.LeadSessionId, ct);
        var areaLead = await kernel.OpenMcpCallerAsync(area.LeadSessionId, ct);

        string[] reads = ["memory_list", "memory_read", "memory_recall"];
        Assert.Equal(reads.Append("memory_propose"), await ListToolNamesAsync(host, McpPath, lead.Token, ct));
        Assert.Equal(reads.Concat(["memory_put", "memory_delete"]), await ListToolNamesAsync(host, McpPath, globalLead.Token, ct));
        Assert.Equal(reads.Concat(["memory_put", "memory_delete"]), await ListToolNamesAsync(host, McpPath, areaLead.Token, ct));
        Assert.True((await CallToolAsync(host, McpPath, lead.Token, "memory_put", Entry("ordinary"), ct)).IsError);
        Assert.True((await CallToolAsync(host, McpPath, lead.Token, "memory_delete", new { key = "ordinary" }, ct)).IsError);
        Assert.True((await CallToolAsync(host, McpPath, globalLead.Token, "memory_propose",
            new { scope = "global", summary = "Suggestion", details = "Details" }, ct)).IsError);
        Assert.True((await CallToolAsync(host, McpPath, areaLead.Token, "memory_put", Entry("outside", scope: "global"), ct)).IsError);
        Assert.True((await CallToolAsync(host, McpPath, areaLead.Token, "memory_delete", new { key = "outside", scope = "global" }, ct)).IsError);

        using var events = kernel.EventBus.Subscribe();
        var invalid = await CallToolAsync(host, McpPath, areaLead.Token, "memory_put", Entry("../escape"), ct);
        Assert.True(invalid.IsError);
        Assert.Contains("memory_validation", invalid.Text);
        var overflow = await CallToolAsync(host, McpPath, areaLead.Token, "memory_put", Entry("large", new string('X', 2000), pinned: true), ct);
        Assert.True(overflow.IsError);
        Assert.Contains("pinnedBudget", overflow.Text);
        Assert.False(events.Reader.TryRead(out _));

        await SuccessfulCallAsync(host, globalLead, "memory_put", Entry("shared", "Global reference"), ct);
        var globalUpdate = await events.Reader.ReadAsync(ct);
        Assert.Null(globalUpdate.EventId);
        Assert.Equal(KernelEventKinds.MemoryUpdated, globalUpdate.Kind);
        Assert.Equal("global", globalUpdate.Payload.GetProperty("scope").GetString());
        Assert.Equal(global.TrackId, globalUpdate.Payload.GetProperty("trackId").GetString());
        Assert.Equal("shared", globalUpdate.Payload.GetProperty("key").GetString());
        var put = await SuccessfulCallAsync(host, areaLead, "memory_put", Entry("local", "Area reference"), ct);
        var areaUpdate = await events.Reader.ReadAsync(ct);
        Assert.Equal(userTrack.AreaId, areaUpdate.AreaId);
        Assert.Equal(area.TrackId, areaUpdate.Payload.GetProperty("trackId").GetString());
        Assert.Equal("put", areaUpdate.Payload.GetProperty("action").GetString());

        var readable = await SuccessfulCallAsync(host, areaLead, "memory_read", new { refs = new[] { "global/shared", "area/local" } }, ct);
        Assert.Equal(["area/local", "global/shared"], readable.GetProperty("entries").EnumerateArray().Select(item => item.GetProperty("ref").GetString()));
        var denied = await CallToolAsync(host, McpPath, globalLead.Token, "memory_read", new { refs = new[] { "area/local" }, areaId = userTrack.AreaId }, ct);
        Assert.True(denied.IsError);
        var globalRead = await SuccessfulCallAsync(host, globalLead, "memory_read", new { refs = new[] { "area/local" } }, ct);
        Assert.Empty(globalRead.GetProperty("entries").EnumerateArray());
        Assert.Equal("area/local", Assert.Single(globalRead.GetProperty("notFound").EnumerateArray()).GetString());

        var etag = put.GetProperty("etag").GetString();
        Assert.True((await CallToolAsync(host, McpPath, areaLead.Token, "memory_delete", new { key = "local" }, ct)).IsError);
        await SuccessfulCallAsync(host, areaLead, "memory_delete", new { key = "local", expectedEtag = etag }, ct);
        var deletion = await events.Reader.ReadAsync(ct);
        Assert.Null(deletion.EventId);
        Assert.Equal(area.TrackId, deletion.Payload.GetProperty("trackId").GetString());
        Assert.Equal("delete", deletion.Payload.GetProperty("action").GetString());

        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeTurn = async (input, token) =>
        {
            if (!input.Prompt.Contains("memory worker probe", StringComparison.Ordinal)) return;
            workerStarted.TrySetResult();
            await releaseWorker.Task.WaitAsync(token);
        };
        try
        {
            var (workerSession, _) = await FlowFixture.StartWorkerAsync(kernel, trackId, "memory-worker", "memory worker probe", ct);
            await workerStarted.Task.WaitAsync(ct);
            var worker = kernel.RuntimeCaller(workerSession.Id);
            Assert.NotNull(worker.BoundTaskId);
            Assert.Equal(reads, await ListToolNamesAsync(host, McpPath, worker.Token, ct));
            Assert.True((await CallToolAsync(host, McpPath, worker.Token, "memory_propose",
                new { scope = "area", summary = "Suggestion", details = "Details" }, ct)).IsError);
            await SuccessfulCallAsync(host, worker, "memory_list", new { }, ct);
        }
        finally
        {
            releaseWorker.TrySetResult();
        }
    }

    [Fact]
    public async Task ProposalWakesTheMemoryPlannerThroughDurableMail()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var track = (await kernel.GetTrackAggregateAsync(trackId, ct)).Track;
        var lead = await kernel.OpenMcpCallerAsync(sessionId, ct);

        var proposal = await SuccessfulCallAsync(host, lead, "memory_propose",
            new { scope = "area", summary = "Capture a durable decision", details = "Always prefer the reviewed dataset.", suggestedPinned = true }, ct);
        var target = await kernel.GetOrEnsureMemoryTrackAsync(track.AreaId, ct);
        Assert.Equal(target.TrackId, proposal.GetProperty("trackId").GetString());
        await kernel.WaitForTrackIdleAsync(target.TrackId, ct);
        var mail = Assert.Single(await kernel.ListMailsAsync(target.TrackId, ct: ct));
        Assert.Equal(mail.Id, proposal.GetProperty("mailId").GetString());
        Assert.Equal(trackId, mail.FromTrackId);
        var turn = Assert.Single(host.AgentRuntime.ExecutedTurns, item => item.Input.Prompt.Contains("reviewed dataset", StringComparison.Ordinal));
        Assert.Contains("Suggested pinned: true", turn.Input.Prompt);
        Assert.Contains("You are the Memory Planner", turn.Spec.SystemPrompt);
        var server = Assert.IsType<McpServerConfig.HttpMcpServer>(turn.Spec.McpServers["perga_memory"]);
        Assert.EndsWith(McpPath, server.Url, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(host.DataDir, "workspaces", target.TrackId), turn.Spec.Cwd);
        Assert.False(Directory.Exists(Path.Combine(turn.Spec.Cwd, ".git")));
        var snapshot = await kernel.GetTrackAggregateAsync(target.TrackId, ct);
        Assert.Equal("memory", snapshot.Track.TemplateId);
        Assert.Empty(snapshot.Track.Data.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task LeadLoadsMemoryWhenChangedAndOnlyOwnerMessagesHydrateMentions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var areaId = (await kernel.GetTrackAggregateAsync(trackId, ct)).Track.AreaId;
        var global = await kernel.GetOrEnsureMemoryTrackAsync(null, ct);
        var area = await kernel.GetOrEnsureMemoryTrackAsync(areaId, ct);
        var globalLead = await kernel.OpenMcpCallerAsync(global.LeadSessionId, ct);
        var areaLead = await kernel.OpenMcpCallerAsync(area.LeadSessionId, ct);

        var empty = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Empty memory", ct);
        Assert.Equal("Empty memory", empty.Input.Prompt);
        var pinned = await SuccessfulCallAsync(host, areaLead, "memory_put", Entry("team", "Area pinned body", pinned: true), ct);
        await SuccessfulCallAsync(host, areaLead, "memory_put", Entry("topic", "Workspace topic body"), ct);
        await SuccessfulCallAsync(host, globalLead, "memory_put", Entry("shared", "Catalogued kestrels are reviewed annually."), ct);

        var first = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "First with entries", ct);
        Assert.StartsWith("<memory_context>\n", first.Input.Prompt, StringComparison.Ordinal);
        Assert.Contains("Area pinned body", first.Input.Prompt);
        Assert.Contains("area/topic — topic summary", first.Input.Prompt);
        Assert.DoesNotContain("Workspace topic body", first.Input.Prompt);
        Assert.DoesNotContain("<memory_context>", first.Spec.SystemPrompt ?? "");
        var unchanged = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Unchanged", ct);
        Assert.Equal("Unchanged", unchanged.Input.Prompt);

        await SuccessfulCallAsync(host, areaLead, "memory_put", new
        {
            key = "team", title = "team", summary = "team summary", body = "Revised area pinned body", pinned = true,
            expectedEtag = pinned.GetProperty("etag").GetString()
        }, ct);
        var refreshed = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Refresh", ct);
        Assert.Contains("Revised area pinned body", refreshed.Input.Prompt);
        var page = await kernel.GetSessionItemsAsync(sessionId, new(Limit: 100), Owner, ct);
        Assert.Equal(["loaded", "refreshed"], page.Items.Where(item => item.Kind == "system" &&
                item.Payload.TryGetProperty("kind", out var kind) && kind.GetString() == "memory_context")
            .Select(item => item.Payload.GetProperty("mode").GetString()));

        var explicitRef = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Read @`memory:area/topic`", ct);
        Assert.StartsWith("<explicit_memory>\n", explicitRef.Input.Prompt, StringComparison.Ordinal);
        Assert.Contains("Workspace topic body", explicitRef.Input.Prompt);
        var scopes = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Read @`memory:global` and @`memory:area`", ct);
        Assert.Contains("Catalogued kestrels", scopes.Input.Prompt);
        Assert.True(scopes.Input.Prompt.IndexOf("area/team", StringComparison.Ordinal) <
            scopes.Input.Prompt.IndexOf("global/shared", StringComparison.Ordinal));
        var recalled = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "@memory kestrels", ct);
        Assert.Contains("Catalogued kestrels", recalled.Input.Prompt);

        await kernel.SendMailAsync(area.TrackId, area.LeadSessionId, trackId, "Untrusted wake",
            "Do @`memory:global/shared`", ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var wake = Assert.Single(host.AgentRuntime.ExecutedTurns, item => item.Input.Prompt.Contains("Untrusted wake", StringComparison.Ordinal));
        Assert.DoesNotContain("<explicit_memory>", wake.Input.Prompt);
        Assert.DoesNotContain("Catalogued kestrels", wake.Input.Prompt);

        var lead = kernel.RuntimeCaller(sessionId);
        var created = await CallToolAsync(host, "/mcp/kernel", lead.Token, KernelMcpToolNames.SessionCreate,
            new { title = "Agent-authored prompt", prompt = "Agent says @`memory:global/shared`", requestKey = "agent-first-memory" }, ct);
        Assert.False(created.IsError, created.Text);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var agentPrompt = Assert.Single(host.AgentRuntime.ExecutedTurns, item => item.Input.Prompt.Contains("Agent says", StringComparison.Ordinal));
        Assert.DoesNotContain("<explicit_memory>", agentPrompt.Input.Prompt);

        await kernel.ControlSessionAsync(sessionId, new("compact"), Owner, ct);
        var newEpoch = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "After compaction", ct);
        Assert.Contains("Revised area pinned body", newEpoch.Input.Prompt);

        var planner = await SendAndReadTurnAsync(host, kernel, area.TrackId, area.LeadSessionId, "Continuing curation", ct);
        Assert.Equal("Continuing curation", planner.Input.Prompt);
    }

    [Fact]
    public async Task NativeSteeringHydratesOnlyOwnerAuthoredMemoryMentions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, leadId) = await host.CreateTrackAsync();
        var lead = await kernel.OpenMcpCallerAsync(leadId, ct);
        var engine = host.Services.GetRequiredService<IMemoryEngine>();
        await engine.PutAsync(new(MemoryScopeKind.Global),
            new("steering", "Steering", "Steering context", "The full memory body."), ct);
        var (child, _) = await kernel.CreateSessionAsync(trackId, new(Title: "Steering target"), "steering-target", Owner, ct);
        var sessionId = child.Session.Id;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<string>();
        host.AgentRuntime.BeforeTurn = async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        host.AgentRuntime.SetSteerHandler(prompt =>
        {
            delivered.Add(prompt);
            return Task.FromResult(true);
        });

        try
        {
            var run = await kernel.SendSessionMessageAsync(sessionId, new("Hold the turn", "hold-turn"), Owner, ct);
            await started.Task.WaitAsync(ct);
            const string ownerContent = "Owner steering @`memory:global/steering`";
            var ownerSteer = await kernel.SendSessionMessageAsync(sessionId,
                new(ownerContent, "owner-steer", Mode: "steer", TurnId: run.TurnId), Owner, ct);
            Assert.Equal("delivered", ownerSteer.Delivery);

            const string agentContent = "Agent steering @`memory:global/steering`";
            var agentSteer = await CallToolAsync(host, "/mcp/kernel", lead.Token, KernelMcpToolNames.SessionSend,
                new { sessionId, content = agentContent, requestKey = "agent-steer", mode = "steer", turnId = run.TurnId }, ct);
            Assert.False(agentSteer.IsError, agentSteer.Text);
            using var result = JsonDocument.Parse(agentSteer.Text);
            Assert.Equal("delivered", result.RootElement.GetProperty("delivery").GetString());

            Assert.Collection(delivered, prompt =>
            {
                Assert.StartsWith("<explicit_memory>\n", prompt, StringComparison.Ordinal);
                Assert.Contains("The full memory body.", prompt);
                Assert.EndsWith(ownerContent, prompt, StringComparison.Ordinal);
            }, prompt => Assert.Equal(agentContent, prompt));
            var messages = host.Services.GetRequiredService<ITrackStore>().SessionMessages!;
            var ownerMessage = await messages.FindAsync(sessionId, SessionMessageAuthors.Owner, "owner-steer", ct);
            var agentMessage = await messages.FindAsync(sessionId, leadId, "agent-steer", ct);
            Assert.Equal(ownerContent, ownerMessage?.Content);
            Assert.Equal(agentContent, agentMessage?.Content);
        }
        finally
        {
            release.TrySetResult();
        }
        await kernel.WaitForTrackIdleAsync(trackId, ct);
    }

    [Fact]
    public async Task MentionHydrationPrioritizesExactRefsAndReportsMissingAndOmittedEntries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var engine = host.Services.GetRequiredService<IMemoryEngine>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var areaId = (await kernel.GetTrackAggregateAsync(trackId, ct)).Track.AreaId;
        var expectedBody = "Exact detail. " + new string('E', 3850);
        var recallBody = "kiwimarker Recall detail. " + new string('R', 3500);

        await engine.PutAsync(new(MemoryScopeKind.Area, areaId),
            new("general", "General", "Whole scope candidate", "Scope detail. " + new string('A', 5500)), ct);
        await engine.PutAsync(new(MemoryScopeKind.Global),
            new("kiwimarker-exact", "kiwimarker exact", "kiwimarker exact", expectedBody), ct);
        await engine.PutAsync(new(MemoryScopeKind.Global), new("recall", "Recall", "Recall candidate", recallBody), ct);
        await engine.PutAsync(new(MemoryScopeKind.Global),
            new("oversize", "Oversize", "Recall candidate", "kiwimarker Oversized detail. " + new string('O', 5000)), ct);

        var turn = await SendAndReadTurnAsync(host, kernel, trackId, sessionId,
            "@`memory:area` @`memory:global/kiwimarker-exact` @`memory:area/missing` @memory kiwimarker", ct);
        var prompt = turn.Input.Prompt;
        Assert.StartsWith("<explicit_memory>\n", prompt, StringComparison.Ordinal);
        var explicitContext = prompt[..prompt.IndexOf("</explicit_memory>", StringComparison.Ordinal)];
        Assert.Contains(expectedBody, explicitContext);
        Assert.Contains(recallBody, explicitContext);
        Assert.DoesNotContain("Scope detail.", explicitContext);
        Assert.DoesNotContain("Oversized detail.", explicitContext);
        Assert.Contains("Not found: area/missing", explicitContext);
        Assert.Contains("2 additional entries omitted due to budget", explicitContext);

        var missing = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "@`memory:global/missing`", ct);
        Assert.StartsWith("<explicit_memory>\nNot found: global/missing\n</explicit_memory>\n",
            missing.Input.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitMemoryCountsAllRenderedUtf8BytesAndCapsMissingReferences()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var provider = host.Services.GetRequiredService<IMemoryContextProvider>();
        var engine = host.Services.GetRequiredService<IMemoryEngine>();
        var missing = Enumerable.Range(0, 160)
            .Select(i => $"@`memory:global/{i:D3}{new string('x', 61)}`");
        var mentions = string.Join(' ', missing);

        // Also check mixed results, where headings and status lines compete with multibyte bodies.
        var body = new string('文', 2500);
        await engine.PutAsync(new(MemoryScopeKind.Global), new("large", "Large", "Large memory", body), ct);
        await engine.PutAsync(new(MemoryScopeKind.Global), new("small", "Small", "Small memory", "Included body"), ct);
        var absentOnly = await provider.HydrateMentionsAsync(null, mentions, ct);
        Assert.StartsWith("<explicit_memory>\nNot found: global/000", absentOnly, StringComparison.Ordinal);
        Assert.Contains("(+152 more)", absentOnly);
        Assert.DoesNotContain("global/008", absentOnly);
        Assert.InRange(Encoding.UTF8.GetByteCount(absentOnly), 1, 8192);

        var rendered = await provider.HydrateMentionsAsync(null, mentions + " @`memory:global/large` @`memory:global/small`", ct);
        Assert.StartsWith("<explicit_memory>\n", rendered, StringComparison.Ordinal);
        Assert.EndsWith("</explicit_memory>\n", rendered, StringComparison.Ordinal);
        Assert.Contains("Included body", rendered);
        Assert.Contains("(+152 more)", rendered);
        Assert.Contains("1 additional entry omitted due to budget", rendered);
        Assert.InRange(Encoding.UTF8.GetByteCount(rendered), 1, 8192);
    }

    [Fact]
    public async Task FailedSnapshotReadKeepsLastDeliveredMemoryVersion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var provider = host.Services.GetRequiredService<IMemoryContextProvider>();
        var engine = host.Services.GetRequiredService<IMemoryEngine>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        Assert.Equal(new MemoryTurnSnapshot("", "", 0), await provider.GetContextSnapshotAsync(null, ct));
        await engine.PutAsync(new(MemoryScopeKind.Global), new("rule", "Rule", "Retained rule", "Retained body", Pinned: true), ct);
        var loaded = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Load memory", ct);
        Assert.Contains("Retained body", loaded.Input.Prompt);

        var dir = Path.Combine(host.DataDir, "memory", "global");
        var backup = dir + "-backup";
        Directory.Move(dir, backup);
        await File.WriteAllTextAsync(dir, "A file makes the scope unreadable.", ct);
        Assert.Null(await provider.GetContextSnapshotAsync(null, ct));
        var failed = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Memory unavailable", ct);
        Assert.Equal("Memory unavailable", failed.Input.Prompt);

        File.Delete(dir);
        Directory.Move(backup, dir);
        var recovered = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Memory recovered", ct);
        Assert.Equal("Memory recovered", recovered.Input.Prompt);
        Assert.Equal(["loaded"], await MemoryNoticeModesAsync(kernel, sessionId, ct));
    }

    [Fact]
    public async Task LastEntryDeletionClearsAnOpenLeadContextOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var planner = await kernel.GetOrEnsureMemoryTrackAsync(null, ct);
        var plannerLead = await kernel.OpenMcpCallerAsync(planner.LeadSessionId, ct);
        var saved = await SuccessfulCallAsync(host, plannerLead, "memory_put",
            Entry("last", "One remaining memory entry.", pinned: true), ct);

        var loaded = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Load memory", ct);
        Assert.Contains("One remaining memory entry.", loaded.Input.Prompt);
        await SuccessfulCallAsync(host, plannerLead, "memory_delete",
            new { key = "last", expectedEtag = saved.GetProperty("etag").GetString() }, ct);
        var cleared = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "After deletion", ct);
        Assert.StartsWith(ClearedContext, cleared.Input.Prompt, StringComparison.Ordinal);
        var stillEmpty = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Still empty", ct);
        Assert.Equal("Still empty", stillEmpty.Input.Prompt);

        var page = await kernel.GetSessionItemsAsync(sessionId, new(Limit: 100), Owner, ct);
        var notice = Assert.Single(page.Items, item => item.Kind == "system" &&
            item.Payload.TryGetProperty("mode", out var mode) && mode.GetString() == "cleared");
        Assert.Equal(cleared.Input.CorrelationId, notice.TurnId);
        Assert.Equal(0, notice.Payload.GetProperty("entryCount").GetInt32());
    }

    [Fact]
    public async Task RestartRecoversLastMemoryDeliveryAndPersistsTheClear()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        var directory = Directory.CreateTempSubdirectory("perga-memory-clear-restart-");
        try
        {
            string trackId;
            string sessionId;
            string nativeThreadId;
            int epoch;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                (trackId, sessionId) = await host.CreateTrackAsync();
                await LoadThenDeleteMemoryAsync(host, kernel, trackId, sessionId, ct);
                var session = (await kernel.GetSessionItemsAsync(sessionId, new(), Owner, ct)).Session;
                nativeThreadId = Assert.IsType<string>(session.NativeThreadId);
                epoch = session.Epoch;
            }

            await using (var reopened = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = reopened.Services.GetRequiredService<IPergaKernel>();
                var cleared = await SendAndReadTurnAsync(reopened, kernel, trackId, sessionId, "First turn after restart", ct);
                Assert.Equal(nativeThreadId, cleared.Spec.ResumeNativeThreadId);
                Assert.StartsWith(ClearedContext, cleared.Input.Prompt, StringComparison.Ordinal);
                Assert.Equal(epoch, (await kernel.GetSessionItemsAsync(sessionId, new(), Owner, ct)).Session.Epoch);
                Assert.Equal(["loaded", "cleared"], await MemoryNoticeModesAsync(kernel, sessionId, ct));
            }

            await using var reopenedAgain = new PergaHostTestCluster(directory.FullName);
            var restartedKernel = reopenedAgain.Services.GetRequiredService<IPergaKernel>();
            var again = await SendAndReadTurnAsync(reopenedAgain, restartedKernel, trackId, sessionId, "Still empty after restart", ct);
            Assert.Equal("Still empty after restart", again.Input.Prompt);
            Assert.Equal(["loaded", "cleared"], await MemoryNoticeModesAsync(restartedKernel, sessionId, ct));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NativeCompactionRecoversLastMemoryDeliveryInTheSameEpoch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        host.AgentRuntime.CompactHandler = _ => Task.FromResult(new NativeCompactResult());
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await LoadThenDeleteMemoryAsync(host, kernel, trackId, sessionId, ct);
        var before = (await kernel.GetSessionItemsAsync(sessionId, new(), Owner, ct)).Session;
        Assert.NotNull(before.NativeThreadId);
        await kernel.ControlSessionAsync(sessionId, new("compact"), Owner, ct);
        var after = (await kernel.GetSessionItemsAsync(sessionId, new(), Owner, ct)).Session;
        Assert.Equal(before.Epoch, after.Epoch);
        Assert.Equal(before.NativeThreadId, after.NativeThreadId);

        var cleared = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "First turn after compaction", ct);
        Assert.StartsWith(ClearedContext, cleared.Input.Prompt, StringComparison.Ordinal);
        var again = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Still empty", ct);
        Assert.Equal("Still empty", again.Input.Prompt);
        Assert.Equal(["loaded", "cleared"], await MemoryNoticeModesAsync(kernel, sessionId, ct));
    }

    [Fact]
    public async Task BootRemovesOrphanAreaMemoryAndScavengesStaleTemporaryFiles()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        var directory = Directory.CreateTempSubdirectory("perga-memory-cleanup-");
        try
        {
            string orphanDirectory;
            string activeDirectory;
            var root = Path.Combine(directory.FullName, "memory");
            var globalDirectory = Path.Combine(root, "global");
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var engine = host.Services.GetRequiredService<IMemoryEngine>();
                var orphan = await kernel.CreateAreaAsync(new("Deleted before file cleanup"), ct);
                var active = await kernel.CreateAreaAsync(new("Keep this Area"), ct);
                var entry = new MemoryPutRequest("retained", "Stored entry", "Stored entry summary", "Memory body");
                await engine.PutAsync(new(MemoryScopeKind.Global), entry, ct);
                await engine.PutAsync(new(MemoryScopeKind.Area, orphan.Id), entry, ct);
                await engine.PutAsync(new(MemoryScopeKind.Area, active.Id), entry, ct);
                orphanDirectory = Path.Combine(root, "areas", orphan.Id);
                activeDirectory = Path.Combine(root, "areas", active.Id);

                foreach (var scope in new[] { globalDirectory, activeDirectory })
                {
                    var stale = Path.Combine(scope, ".stale.tmp");
                    await File.WriteAllTextAsync(stale, "Interrupted write", ct);
                    File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-10));
                    await File.WriteAllTextAsync(Path.Combine(scope, ".recent.tmp"), "Recent write", ct);
                }

                // Simulate a crash after the DB commit and before Kernel directory teardown.
                await host.Services.GetRequiredService<ITrackStore>().DeleteAreaAsync(orphan.Id, ct);
                Assert.True(File.Exists(Path.Combine(orphanDirectory, "retained.md")));
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            Assert.False(Directory.Exists(orphanDirectory));
            foreach (var scope in new[] { globalDirectory, activeDirectory })
            {
                Assert.True(File.Exists(Path.Combine(scope, "retained.md")));
                Assert.True(File.Exists(Path.Combine(scope, ".recent.tmp")));
                Assert.False(File.Exists(Path.Combine(scope, ".stale.tmp")));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MentionSearchNormalizesFullWidthColonAndCapsAreaFirstResults()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var engine = host.Services.GetRequiredService<IMemoryEngine>();
        var area = await kernel.CreateAreaAsync(new("Mention search"), ct);
        var areaScope = new MemoryWriteScope(MemoryScopeKind.Area, area.Id);
        var globalScope = new MemoryWriteScope(MemoryScopeKind.Global);
        var path = $"/mentions?areaId={area.Id}";
        Assert.Empty((await GetAsync(host, path, ct)).EnumerateArray());

        await engine.PutAsync(areaScope, new("review-pinned", "Pinned review", "Review", "Pinned detail", Pinned: true), ct);
        Assert.Equal(["area", "area/review-pinned"], (await GetAsync(host, path, ct)).EnumerateArray()
            .Select(item => item.GetProperty("ref").GetString()));

        for (var i = 0; i < 21; i++)
            await engine.PutAsync(areaScope, new($"review-area-{i:D2}", "Area review", "Review", "Area detail"), ct);
        for (var i = 0; i < 8; i++)
            await engine.PutAsync(globalScope, new($"review-global-{i:D2}", "Global review", "Review", "Global detail"), ct);

        var fullWidth = await GetAsync(host, $"{path}&q={Uri.EscapeDataString(" memory：review ")}", ct);
        var ascii = await GetAsync(host, $"{path}&q=memory%3Areview", ct);
        Assert.True(JsonElement.DeepEquals(ascii, fullWidth));
        var refs = fullWidth.EnumerateArray().Select(item => item.GetProperty("ref").GetString()!).ToArray();
        Assert.Equal(24, refs.Length);
        Assert.Equal("area/review-pinned", refs[0]);
        Assert.All(refs.Take(22), reference => Assert.StartsWith("area/", reference, StringComparison.Ordinal));
        Assert.All(refs.Skip(22), reference => Assert.StartsWith("global/", reference, StringComparison.Ordinal));

        var unfiltered = await GetAsync(host, path, ct);
        Assert.Equal(24, unfiltered.GetArrayLength());
        Assert.Equal("area", unfiltered[0].GetProperty("ref").GetString());
        Assert.Equal("global", unfiltered[23].GetProperty("ref").GetString());
    }

    [Fact]
    public async Task MemoryTracksAndScopeBindingsSurviveRestartAndAreaDeletionCleansUp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        var directory = Directory.CreateTempSubdirectory("perga-memory-restart-");
        try
        {
            MemoryPlannerTrackDto areaPlanner;
            MemoryPlannerTrackDto globalPlanner;
            string areaId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var area = await kernel.CreateAreaAsync(new("Local"), ct);
                areaId = area.Id;
                using var unauthenticated = await host.Client.PostAsync(MemoryPath + "/global/track", null, ct);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
                Assert.Empty((await GetAsync(host, "/mentions", ct)).EnumerateArray());
                Assert.Null(await host.Services.GetRequiredService<IMemoryTrackStore>().GetMemoryTrackIdAsync("global", ct));

                globalPlanner = await EnsureAsync(host, "/global/track", ct);
                areaPlanner = await EnsureAsync(host, $"/areas/{areaId}/track", ct);
                var globalLead = await kernel.OpenMcpCallerAsync(globalPlanner.LeadSessionId, ct);
                var areaLead = await kernel.OpenMcpCallerAsync(areaPlanner.LeadSessionId, ct);
                await SuccessfulCallAsync(host, globalLead, "memory_put", Entry("global-rule", "# Global rule"), ct);
                await SuccessfulCallAsync(host, areaLead, "memory_put", Entry("area-rule", "# Area rule\n\n<!-- preserve me -->", pinned: true), ct);

                var sender = await kernel.CreateTrackAsync(new(areaId, Title: "Proposal source"), ct);
                var caller = await kernel.OpenMcpCallerAsync(sender.Track.LeadSessionId!, ct);
                kernel.DispatchAdmission.SetDraining(true);
                await SuccessfulCallAsync(host, caller, "memory_propose",
                    new { scope = "area", summary = "Pending before restart", details = "Recover this durable proposal." }, ct);
                var wakes = host.Services.GetRequiredService<ITrackStore>().TrackWakes;
                Assert.Contains(areaPlanner.TrackId, await wakes.ListPendingTracksAsync(ct));
                Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns,
                    item => item.Input.Prompt.Contains("Recover this durable proposal.", StringComparison.Ordinal));
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var restartedKernel = reopened.Services.GetRequiredService<IPergaKernel>();
            await restartedKernel.WaitForTrackIdleAsync(areaPlanner.TrackId, ct);
            Assert.Single(reopened.AgentRuntime.ExecutedTurns,
                item => item.Input.Prompt.Contains("Recover this durable proposal.", StringComparison.Ordinal));
            Assert.Equal(globalPlanner, await EnsureAsync(reopened, "/global/track", ct));
            Assert.Equal(areaPlanner, await EnsureAsync(reopened, $"/areas/{areaId}/track", ct));
            var global = (await ReadTrackAsync(reopened, globalPlanner, ct)).GetProperty("track").GetProperty("data");
            Assert.Equal(JsonValueKind.Null, global.GetProperty("areaId").ValueKind);
            Assert.Equal(820, global.GetProperty("pinnedBudgetBytes").GetInt32());
            Assert.Equal("# Global rule", Assert.Single(global.GetProperty("entries").EnumerateArray()).GetProperty("body").GetString());
            var areaPage = (await ReadTrackAsync(reopened, areaPlanner, ct)).GetProperty("track").GetProperty("data");
            Assert.Equal(1, areaPage.GetProperty("globalEntryCount").GetInt32());
            Assert.Equal(1228, areaPage.GetProperty("pinnedBudgetBytes").GetInt32());
            Assert.True(areaPage.GetProperty("pinnedUsedBytes").GetInt64() > 0);
            var stored = Assert.Single(areaPage.GetProperty("entries").EnumerateArray());
            Assert.Equal("# Area rule\n\n<!-- preserve me -->", stored.GetProperty("body").GetString());
            Assert.True(stored.GetProperty("sizeBytes").GetInt32() > stored.GetProperty("body").GetString()!.Length);
            var mentions = (await GetAsync(reopened, $"/mentions?areaId={areaId}", ct)).EnumerateArray().ToArray();
            Assert.Equal(["area", "area/area-rule", "global", "global/global-rule"], mentions.Select(item => item.GetProperty("ref").GetString()));
            Assert.Equal("@`memory:area/area-rule`", mentions[1].GetProperty("insert").GetString());

            var areaDirectory = Path.Combine(directory.FullName, "memory", "areas", areaId);
            Assert.True(File.Exists(Path.Combine(areaDirectory, "area-rule.md")));
            using var delete = await reopened.AuthenticatedClient.DeleteAsync($"/api/v1/areas/{areaId}", ct);
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.False(Directory.Exists(areaDirectory));
            Assert.False(Directory.Exists(Path.Combine(directory.FullName, "workspaces", areaPlanner.TrackId)));
            Assert.False(restartedKernel.Actors.IsTrackActiveInMemory(areaPlanner.TrackId));
            Assert.Null(await reopened.Services.GetRequiredService<ITrackReadStore>().GetTrackAsync(areaPlanner.TrackId, ct));
            Assert.Equal(globalPlanner, await EnsureAsync(reopened, "/global/track", ct));
            using var missing = await reopened.AuthenticatedClient.PostAsync($"{MemoryPath}/areas/{areaId}/track", null, ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static object Entry(string key, string body = "Memory entry", bool pinned = false, string? scope = null) =>
        scope is null ? new { key, title = key, summary = key + " summary", body, pinned }
            : new { key, title = key, summary = key + " summary", body, pinned, scope };

    private static async Task<JsonElement> SuccessfulCallAsync(PergaHostTestCluster host, McpSessionPrincipal caller,
        string name, object args, CancellationToken ct)
    {
        var (text, error) = await CallToolAsync(host, McpPath, caller.Token, name, args, ct);
        Assert.False(error, text);
        using var result = JsonDocument.Parse(text);
        return result.RootElement.Clone();
    }

    private static async Task<RecordedTurnExecution> SendAndReadTurnAsync(PergaHostTestCluster host, IPergaKernel kernel,
        string trackId, string sessionId, string prompt, CancellationToken ct)
    {
        var receipt = await kernel.SendSessionMessageAsync(sessionId, new(prompt, Guid.NewGuid().ToString("N")), Owner, ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        return Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.CorrelationId == receipt.TurnId);
    }

    private static async Task LoadThenDeleteMemoryAsync(PergaHostTestCluster host, IPergaKernel kernel,
        string trackId, string sessionId, CancellationToken ct)
    {
        var engine = host.Services.GetRequiredService<IMemoryEngine>();
        var scope = new MemoryWriteScope(MemoryScopeKind.Global);
        var entry = await engine.PutAsync(scope, new("last", "Last", "Last entry", "One remaining entry.", Pinned: true), ct);
        var loaded = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Load memory", ct);
        Assert.Contains("One remaining entry.", loaded.Input.Prompt);
        await engine.DeleteAsync(scope, "last", entry.Meta.Etag, ct);
    }

    private static async Task<string[]> MemoryNoticeModesAsync(IPergaKernel kernel, string sessionId, CancellationToken ct)
    {
        var page = await kernel.GetSessionItemsAsync(sessionId, new(Limit: 100), Owner, ct);
        return page.Items.Where(item => item.Kind == "system" &&
                item.Payload.TryGetProperty("kind", out var kind) && kind.GetString() == "memory_context")
            .Select(item => Assert.IsType<string>(item.Payload.GetProperty("mode").GetString())).ToArray();
    }

    private static async Task<JsonElement> GetAsync(PergaHostTestCluster host, string path, CancellationToken ct)
    {
        using var response = await host.AuthenticatedClient.GetAsync(MemoryPath + path, ct);
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return result.RootElement.Clone();
    }

    private static Task<JsonElement> ReadTrackAsync(PergaHostTestCluster host, MemoryPlannerTrackDto binding, CancellationToken ct) =>
        host.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{binding.TrackId}", ct);

    private static async Task<MemoryPlannerTrackDto> EnsureAsync(PergaHostTestCluster host, string path, CancellationToken ct)
    {
        using var response = await host.AuthenticatedClient.PostAsync(MemoryPath + path, null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"Memory Track ensure failed ({response.StatusCode}): {body}");
        return JsonSerializer.Deserialize<MemoryPlannerTrackDto>(body, PergaJsonDefaults.ApiOptions)!;
    }
}
