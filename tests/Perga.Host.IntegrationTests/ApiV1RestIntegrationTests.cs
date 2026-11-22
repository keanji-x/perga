using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.ReportDoc;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests;

public sealed class ApiV1RestIntegrationTests
{
    [Fact]
    public async Task Bootstrap_Templates_Areas_And_Tracks_AllRestRoutes_And_WireConventions_WorkEndToEnd()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;

        // 1. GET /api/v1/bootstrap & GET /api/v1/templates
        using var bootResp = await client.GetAsync("/api/v1/bootstrap");
        Assert.Equal(HttpStatusCode.OK, bootResp.StatusCode);
        using var bootDoc = JsonDocument.Parse(await bootResp.Content.ReadAsStringAsync());
        Assert.Equal("1.0.0", bootDoc.RootElement.GetProperty("version").GetString());
        Assert.True(bootDoc.RootElement.GetProperty("providers").GetArrayLength() >= 3);
        var templatesArr = bootDoc.RootElement.GetProperty("templates");
        Assert.True(templatesArr.GetArrayLength() >= 3);

        using var tplResp = await client.GetAsync("/api/v1/templates");
        Assert.Equal(HttpStatusCode.OK, tplResp.StatusCode);
        using var tplDoc = JsonDocument.Parse(await tplResp.Content.ReadAsStringAsync());
        var tplIds = tplDoc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        Assert.Contains("agents-delivery", tplIds);
        Assert.Contains("okr-loop", tplIds);
        Assert.Contains("equity-research", tplIds);

        // 2. Areas CRUD: POST (201), GET, PATCH, validation 400, 404
        using var invalidAreaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAreaResp.StatusCode);
        using var invalidAreaJson = JsonDocument.Parse(await invalidAreaResp.Content.ReadAsStringAsync());
        Assert.Equal("invalid_argument", invalidAreaJson.RootElement.GetProperty("code").GetString());

        var workspaceDir = Path.Combine(cluster.DataDir, "demo-repo");
        Directory.CreateDirectory(workspaceDir);
        await File.WriteAllTextAsync(Path.Combine(workspaceDir, "README.md"), "# Demo Repo\n");

        using var createAreaResp = await client.PostAsJsonAsync(
            "/api/v1/areas",
            new { name = "Core Engineering", color = "#3b82f6", defaultCwd = workspaceDir, folders = new[] { workspaceDir } });
        Assert.Equal(HttpStatusCode.Created, createAreaResp.StatusCode);
        var area = await createAreaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions);
        Assert.NotNull(area);
        Assert.Equal("Core Engineering", area!.Name);

        using var listAreasResp = await client.GetAsync("/api/v1/areas");
        Assert.Equal(HttpStatusCode.OK, listAreasResp.StatusCode);
        var areas = await listAreasResp.Content.ReadFromJsonAsync<List<AreaDto>>(PergaJsonDefaults.ApiOptions);
        Assert.Single(areas!);

        using var patchAreaResp = await client.PatchAsJsonAsync(
            $"/api/v1/areas/{area.Id}",
            new { name = "Platform Engineering", color = "#10b981" });
        Assert.Equal(HttpStatusCode.OK, patchAreaResp.StatusCode);
        var patchedArea = await patchAreaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions);
        Assert.Equal("Platform Engineering", patchedArea!.Name);

        // 3. Tracks CRUD: POST /tracks (201 TrackAggregateSnapshot), GET /areas/{id}/tracks, GET /tracks/{id}, PATCH, DELETE (204)
        using var createTrackResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new
            {
                areaId = area.Id,
                title = "Host v1 Track",
                cwd = workspaceDir,
                workspaceDriver = "direct",
                provider = "codex"
            });
        Assert.Equal(HttpStatusCode.Created, createTrackResp.StatusCode);
        using var snapDoc = JsonDocument.Parse(await createTrackResp.Content.ReadAsStringAsync());
        var trackId = snapDoc.RootElement.GetProperty("track").GetProperty("id").GetString()!;
        Assert.StartsWith("trk_", trackId, StringComparison.Ordinal);
        Assert.Equal(1, snapDoc.RootElement.GetProperty("sessions").GetArrayLength());

        using var getTrackResp = await client.GetAsync($"/api/v1/tracks/{trackId}");
        Assert.Equal(HttpStatusCode.OK, getTrackResp.StatusCode);

        using var patchTrackResp = await client.PatchAsJsonAsync(
            $"/api/v1/tracks/{trackId}",
            new { title = "Renamed Track", pinned = true, closed = true });
        Assert.Equal(HttpStatusCode.OK, patchTrackResp.StatusCode);
        var patchedTrack = await patchTrackResp.Content.ReadFromJsonAsync<TrackDto>(PergaJsonDefaults.ApiOptions);
        Assert.Equal("Renamed Track", patchedTrack!.Title);
        Assert.NotNull(patchedTrack.PinnedAtMs);
        Assert.NotNull(patchedTrack.ClosedAtMs);

        // Open-only vs includeClosed=true
        using var openTracksResp = await client.GetAsync($"/api/v1/areas/{area.Id}/tracks?includeClosed=false");
        var openTracks = await openTracksResp.Content.ReadFromJsonAsync<List<TrackDto>>(PergaJsonDefaults.ApiOptions);
        Assert.Empty(openTracks!);

        using var allTracksResp = await client.GetAsync($"/api/v1/areas/{area.Id}/tracks?includeClosed=true");
        var allTracks = await allTracksResp.Content.ReadFromJsonAsync<List<TrackDto>>(PergaJsonDefaults.ApiOptions);
        Assert.Single(allTracks!);

        // Delete Track (204) -> subsequent GET returns 404
        using var delTrackResp = await client.DeleteAsync($"/api/v1/tracks/{trackId}");
        Assert.Equal(HttpStatusCode.NoContent, delTrackResp.StatusCode);

        using var missingTrackResp = await client.GetAsync($"/api/v1/tracks/{trackId}");
        Assert.Equal(HttpStatusCode.NotFound, missingTrackResp.StatusCode);

        // Delete Area (204)
        using var delAreaResp = await client.DeleteAsync($"/api/v1/areas/{area.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delAreaResp.StatusCode);
    }

    [Fact]
    public async Task Sessions_IdempotencyKey_Steer_Interrupt_Compact_And_AskAnswer_WorkEndToEnd()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;

        var repoDir = Path.Combine(cluster.DataDir, "sess-ws");
        Directory.CreateDirectory(repoDir);

        using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "Sessions", defaultCwd = repoDir });
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions))!;

        using var trackResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new { areaId = area.Id, title = "Session Lifecycle Track", cwd = repoDir });
        using var trackSnap = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync());
        var trackId = trackSnap.RootElement.GetProperty("track").GetProperty("id").GetString()!;
        var leadSessionId = trackSnap.RootElement.GetProperty("track").GetProperty("leadSessionId").GetString()!;

        // 1. POST /api/v1/tracks/{trackId}/sessions (201) + GET /sessions
        var sessionsPath = $"/api/v1/tracks/{trackId}/sessions";
        var createBody = new { title = "Helper Subagent", provider = "claude" };
        using var unkeyed = await client.PostAsJsonAsync(sessionsPath, createBody);
        Assert.Equal(HttpStatusCode.BadRequest, unkeyed.StatusCode);
        using var createSubReq = new HttpRequestMessage(HttpMethod.Post, sessionsPath)
        {
            Content = JsonContent.Create(createBody)
        };
        createSubReq.Headers.Add("Idempotency-Key", "create-subagent");
        using var createSubResp = await client.SendAsync(createSubReq);
        Assert.Equal(HttpStatusCode.Created, createSubResp.StatusCode);
        var created = (await createSubResp.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Null(created.FirstInput);
        var subSession = created.Session;
        Assert.False(subSession.IsLead);

        using var listSessionsResp = await client.GetAsync($"/api/v1/tracks/{trackId}/sessions");
        var sessions = (await listSessionsResp.Content.ReadFromJsonAsync<List<SessionSummaryDto>>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal(2, sessions.Count);

        // 2. POST /api/v1/sessions/{sessionId}/messages requires Idempotency-Key -> 400 when missing!
        using var missingIdemResp = await client.PostAsJsonAsync(
            $"/api/v1/sessions/{leadSessionId}/messages",
            new { content = "Hello without header" });
        Assert.Equal(HttpStatusCode.BadRequest, missingIdemResp.StatusCode);
        using var missingIdemDoc = JsonDocument.Parse(await missingIdemResp.Content.ReadAsStringAsync());
        Assert.Equal("invalid_argument", missingIdemDoc.RootElement.GetProperty("code").GetString());

        // Valid message with Idempotency-Key + duplicate Idempotency-Key deduplication
        using var msgReq1 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
        {
            Content = JsonContent.Create(new { content = "First user turn" })
        };
        msgReq1.Headers.Add("Idempotency-Key", "idem-turn-1");
        using var msgResp1 = await client.SendAsync(msgReq1);
        Assert.Equal(HttpStatusCode.OK, msgResp1.StatusCode);
        var turn1 = (await msgResp1.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("accepted", turn1.Delivery);
        await cluster.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);

        using var msgReqDup = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
        {
            Content = JsonContent.Create(new { content = "First user turn" })
        };
        msgReqDup.Headers.Add("Idempotency-Key", "idem-turn-1");
        using var msgRespDup = await client.SendAsync(msgReqDup);
        var turnDup = (await msgRespDup.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal(turn1.TurnId, turnDup.TurnId);
        Assert.Equal("delivered", turnDup.Delivery);

        // GET /api/v1/sessions/{sessionId}/items?afterId=0&limit=100
        using var itemsResp = await client.GetAsync($"/api/v1/sessions/{leadSessionId}/items?afterId=0&limit=100");
        Assert.Equal(HttpStatusCode.OK, itemsResp.StatusCode);
        using var itemsDoc = JsonDocument.Parse(await itemsResp.Content.ReadAsStringAsync());
        Assert.True(itemsDoc.RootElement.GetProperty("items").GetArrayLength() >= 2);

        // 3. Steer & Interrupt on an in-flight turn
        var turnEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steeredPrompts = new List<string>();
        cluster.AgentRuntime.SetSteerHandler(msg =>
        {
            steeredPrompts.Add(msg);
            return Task.FromResult(true);
        });
        cluster.AgentRuntime.SetTurnHandler(StreamSlowTurnAsync);

        async IAsyncEnumerable<UnifiedAgentEvent> StreamSlowTurnAsync(
            HarnessLaunchSpec spec,
            TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            _ = spec;
            _ = input;
            yield return new UnifiedAgentEvent.TextDelta("Starting slow turn...");
            turnEntered.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }

        var inFlightReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{subSession.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "Long running work" })
        };
        inFlightReq.Headers.Add("Idempotency-Key", "idem-slow-1");
        using var inFlightResponse = await client.SendAsync(inFlightReq);
        Assert.Equal(HttpStatusCode.OK, inFlightResponse.StatusCode);
        var inFlight = (await inFlightResponse.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;

        await turnEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Concurrent default send -> 409 Conflict
        using var busyReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{subSession.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "Concurrent conflict", mode = "default" })
        };
        busyReq.Headers.Add("Idempotency-Key", "idem-busy");
        using var busyResp = await client.SendAsync(busyReq);
        Assert.Equal(HttpStatusCode.Conflict, busyResp.StatusCode);

        using var queueReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{subSession.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "Queue while running", mode = "queue" })
        };
        queueReq.Headers.Add("Idempotency-Key", "idem-queue");
        using var queueResp = await client.SendAsync(queueReq);
        Assert.Equal(HttpStatusCode.OK, queueResp.StatusCode);
        Assert.DoesNotContain("Queue while running", steeredPrompts);
        var queued = (await queueResp.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("queued", queued.Delivery);
        var pending = await client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{subSession.Id}/items", PergaJsonDefaults.ApiOptions);
        Assert.Contains(pending!.QueuedMessages, message => message.Id == queued.MessageId && message.State == "waiting");
        Assert.True(pending.CanSteer);

        using var sendNow = await client.PostAsJsonAsync(
            $"/api/v1/sessions/{subSession.Id}/messages",
            new { messageId = queued.MessageId, mode = "steer", turnId = inFlight.TurnId });
        Assert.Equal(HttpStatusCode.OK, sendNow.StatusCode);
        var sentNow = (await sendNow.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("delivered", sentNow.Delivery);
        Assert.Single(steeredPrompts, text => text == "Queue while running");
        var afterSteer = await client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{subSession.Id}/items", PergaJsonDefaults.ApiOptions);
        Assert.DoesNotContain(afterSteer!.QueuedMessages, message => message.Id == queued.MessageId);
        Assert.Equal("processing", Assert.Single(afterSteer.Items, item => item.Kind == "user" &&
            item.Payload.TryGetProperty("inputId", out var id) && id.GetString() == queued.MessageId).MessageStatus);


        // Explicit steer -> 200 steers active turn!
        using var steerReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{subSession.Id}/messages")
        {
            Content = JsonContent.Create(new { content = "Adjust course now", mode = "steer", turnId = inFlight.TurnId })
        };
        steerReq.Headers.Add("Idempotency-Key", "idem-steer");
        using var steerResp = await client.SendAsync(steerReq);
        Assert.Equal(HttpStatusCode.OK, steerResp.StatusCode);
        Assert.Contains("Adjust course now", steeredPrompts);

        using var supplement = await SendMessageAsync(client, $"/api/v1/sessions/{subSession.Id}/messages", new { mode = "steer", turnId = inFlight.TurnId, content = "Supplement the running turn" });
        Assert.Equal(HttpStatusCode.OK, supplement.StatusCode);
        Assert.Contains("Supplement the running turn", steeredPrompts);

        // POST /api/v1/sessions/{sessionId}/control -> cancels slow turn!
        using var interruptResp = await client.PostAsJsonAsync($"/api/v1/sessions/{subSession.Id}/control", new { action = "stop", turnId = inFlight.TurnId });
        Assert.Equal(HttpStatusCode.OK, interruptResp.StatusCode);

        await cluster.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
        var interruptedTurn = await client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{subSession.Id}/items", PergaJsonDefaults.ApiOptions);
        Assert.Equal("unknown", interruptedTurn!.LastTurn!.Status);
        Assert.Equal(inFlight.TurnId, interruptedTurn.LastTurn!.Id);

        // Restore fast handler & test POST /api/v1/sessions/{sessionId}/control (Epoch 1 -> 2)
        cluster.AgentRuntime.SetTurnHandler(StreamHandoverTurnAsync);

        static async IAsyncEnumerable<UnifiedAgentEvent> StreamHandoverTurnAsync(
            HarnessLaunchSpec spec,
            TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            _ = spec;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new UnifiedAgentEvent.TextDelta($"Handover summary for: {input.Prompt[..Math.Min(25, input.Prompt.Length)]}");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(50, 20, 0, 100_000));
        }

        using var compactResp = await client.PostAsJsonAsync($"/api/v1/sessions/{leadSessionId}/control", new { action = "compact" });
        Assert.Equal(HttpStatusCode.OK, compactResp.StatusCode);
        var compacted = (await compactResp.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal(2, compacted.Epoch);
        Assert.Null(compacted.ContextUsage);

        // 4. POST /api/v1/sessions/{sessionId}/interactions/{askId}/respond ({ answers: [...] } AND { answer: "..." })
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        var askWaitTask = kernel.AskUserAsync(
            trackId,
            subSession.Id,
            [new AskQuestionDto("Which database?", ["SQLite", "Postgres"])]);

        // Poll until openAsk appears on TrackAggregateSnapshot
        AskDto? openAsk = null;
        for (var i = 0; i < 30 && openAsk is null; i++)
        {
            var snap = await kernel.GetTrackAggregateAsync(trackId);
            openAsk = snap.OpenAsks.FirstOrDefault();
            if (openAsk is null)
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(openAsk);
        Assert.Equal(AskStatus.Open, openAsk!.Status);

        using var answerResp = await client.PostAsJsonAsync(
            $"/api/v1/sessions/{openAsk.SessionId}/interactions/{openAsk.Id}/respond",
            new { answers = new[] { "SQLite" } });
        Assert.Equal(HttpStatusCode.OK, answerResp.StatusCode);
        using var answeredDoc = JsonDocument.Parse(await answerResp.Content.ReadAsStringAsync());
        Assert.Equal("ask", answeredDoc.RootElement.GetProperty("kind").GetString());

        var askResult = await askWaitTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("SQLite", askResult[0].GetString());
    }

    [Fact]
    public async Task WorkspaceFiles_RealJjVcsDiff_TerminalsCrud_And_StaticSpaFallback_WorkEndToEnd()
    {
        var distDir = Path.Combine(Path.GetTempPath(), $"perga-spa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(distDir);
        Directory.CreateDirectory(Path.Combine(distDir, "assets"));
        await File.WriteAllTextAsync(Path.Combine(distDir, "index.html"), "<!doctype html><html><body>Perga SPA</body></html>");
        await File.WriteAllTextAsync(Path.Combine(distDir, "app.js"), "console.log('perga');");
        await File.WriteAllTextAsync(Path.Combine(distDir, "assets", "bundle.js"), "console.log('bundle');");
        await File.WriteAllTextAsync(Path.Combine(distDir, "assets", "bundle.css"), "body{margin:0;}");
        await File.WriteAllTextAsync(Path.Combine(distDir, "manifest.webmanifest"), "{\"id\":\"/\"}");

        try
        {
            await using var cluster = new PergaHostTestCluster(
                configureHostOptions: opts => opts.StaticWebRootDirectory = distDir);
            var client = cluster.Client;

            // Initialize a real colocated Jujutsu repository!
            var jjRepo = Path.Combine(cluster.DataDir, "jj-repo");
            Directory.CreateDirectory(jjRepo);
            await RunCliCheckedAsync("jj", ["git", "init", "--colocate"], jjRepo);
            await File.WriteAllTextAsync(Path.Combine(jjRepo, "hello.txt"), "line 1\n");
            await RunCliCheckedAsync("jj", ["commit", "-m", "initial"], jjRepo);
            await File.WriteAllTextAsync(Path.Combine(jjRepo, "hello.txt"), "line 1\nline 2 added\n");

            using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "VCS", defaultCwd = jjRepo });
            var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions))!;

            using var trackResp = await client.PostAsJsonAsync(
                "/api/v1/tracks",
                new { areaId = area.Id, title = "JJ Workspace Track", cwd = jjRepo });
            using var snap = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync());
            var trackId = snap.RootElement.GetProperty("track").GetProperty("id").GetString()!;

            // 1. GET /api/v1/tracks/{trackId}/files (root listing, file read, 403 traversal guard, 404 missing)
            using var dirResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files");
            Assert.Equal(HttpStatusCode.OK, dirResp.StatusCode);
            using var dirDoc = JsonDocument.Parse(await dirResp.Content.ReadAsStringAsync());
            Assert.True(dirDoc.RootElement.GetProperty("isDirectory").GetBoolean());
            Assert.Contains(
                dirDoc.RootElement.GetProperty("entries").EnumerateArray(),
                e => e.GetProperty("name").GetString() == "hello.txt");

            using var fileResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files?path=hello.txt");
            Assert.Equal(HttpStatusCode.OK, fileResp.StatusCode);
            using var fileDoc = JsonDocument.Parse(await fileResp.Content.ReadAsStringAsync());
            Assert.False(fileDoc.RootElement.GetProperty("isDirectory").GetBoolean());
            Assert.Contains("line 2 added", fileDoc.RootElement.GetProperty("content").GetString());

            using var escapeResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files?path=../../etc/passwd");
            Assert.Equal(HttpStatusCode.Forbidden, escapeResp.StatusCode);

            using var missingFileResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files?path=nope.txt");
            Assert.Equal(HttpStatusCode.NotFound, missingFileResp.StatusCode);

            // 2. GET /api/v1/tracks/{trackId}/vcs-diff against real jj working copy
            using var diffResp = await client.GetAsync($"/api/v1/tracks/{trackId}/vcs-diff");
            Assert.Equal(HttpStatusCode.OK, diffResp.StatusCode);
            var diff = (await diffResp.Content.ReadFromJsonAsync<DiffSummary>(PergaJsonDefaults.ApiOptions))!;
            Assert.Contains("hello.txt", diff.ChangedFiles);
            Assert.Contains("line 2 added", diff.DiffText, StringComparison.Ordinal);

            // 3. Terminals CRUD against real perga-term-bridge (POST 201, GET, DELETE 204)
            using var openTermResp = await client.PostAsJsonAsync(
                $"/api/v1/tracks/{trackId}/terminals",
                new { title = "Dev Shell", program = "/bin/sh", idempotencyKey = "term-1" });
            Assert.Equal(HttpStatusCode.Created, openTermResp.StatusCode);
            using var termJson = JsonDocument.Parse(await openTermResp.Content.ReadAsStringAsync());
            var termId = termJson.RootElement.GetProperty("id").GetString()!;
            Assert.Equal("running", termJson.RootElement.GetProperty("status").GetString());

            using var listTermsResp = await client.GetAsync($"/api/v1/tracks/{trackId}/terminals");
            Assert.Equal(HttpStatusCode.OK, listTermsResp.StatusCode);
            var terms = (await listTermsResp.Content.ReadFromJsonAsync<List<TerminalDto>>(PergaJsonDefaults.ApiOptions))!;
            Assert.Single(terms);

            using var delTermResp = await client.DeleteAsync($"/api/v1/tracks/{trackId}/terminals/{termId}");
            Assert.Equal(HttpStatusCode.NoContent, delTermResp.StatusCode);

            // 4. Static assets & SPA fallback without intercepting /api/* or /mcp/*
            using var assetResp = await client.GetAsync("/app.js");
            Assert.Equal(HttpStatusCode.OK, assetResp.StatusCode);
            Assert.Contains("console.log('perga')", await assetResp.Content.ReadAsStringAsync());

            using var rootJsResp = await client.GetAsync("/assets/bundle.js");
            Assert.Equal(HttpStatusCode.OK, rootJsResp.StatusCode);
            Assert.Contains("javascript", rootJsResp.Content.Headers.ContentType?.MediaType ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("console.log('bundle')", await rootJsResp.Content.ReadAsStringAsync());

            using var removedAlias = await client.GetAsync("/next/assets/bundle.js");
            Assert.DoesNotContain("console.log('bundle')", await removedAlias.Content.ReadAsStringAsync());

            using var rootCssResp = await client.GetAsync("/assets/bundle.css");
            Assert.Equal(HttpStatusCode.OK, rootCssResp.StatusCode);
            Assert.Equal("text/css", rootCssResp.Content.Headers.ContentType?.MediaType);

            using var manifestResp = await client.GetAsync("/manifest.webmanifest");
            Assert.Equal(HttpStatusCode.OK, manifestResp.StatusCode);
            Assert.Equal("application/manifest+json", manifestResp.Content.Headers.ContentType?.MediaType);

            using var spaRouteResp = await client.GetAsync($"/tracks/{trackId}");
            Assert.Equal(HttpStatusCode.OK, spaRouteResp.StatusCode);
            Assert.Contains("Perga SPA", await spaRouteResp.Content.ReadAsStringAsync());

            using var api404Resp = await client.GetAsync("/api/v1/does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, api404Resp.StatusCode);
            using var api404Json = JsonDocument.Parse(await api404Resp.Content.ReadAsStringAsync());
            Assert.Equal("not_found", api404Json.RootElement.GetProperty("code").GetString());

            using var mcp404Resp = await client.GetAsync("/mcp/does-not-exist");
            Assert.Equal(HttpStatusCode.NotFound, mcp404Resp.StatusCode);
        }
        finally
        {
            if (Directory.Exists(distDir))
            {
                Directory.Delete(distDir, recursive: true);
            }
        }
    }

    internal static async Task RunCliCheckedAsync(string exe, IReadOnlyList<string> args, string workingDir)
    {
        var psi = new ProcessStartInfo(ResolveUserTool(exe))
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            psi.Environment["PATH"] = $"{Path.Combine(home, ".local", "bin")}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";
        }

        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stderr = await stderrTask;
        _ = await stdoutTask;
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command '{exe} {string.Join(' ', args)}' failed ({proc.ExitCode}): {stderr}");
        }
    }

    private static string ResolveUserTool(string exe)
    {
        if (string.Equals(exe, "jj", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localJj = Path.Combine(home, ".local", "bin", "jj");
            if (File.Exists(localJj))
            {
                return localJj;
            }
        }

        return exe;
    }

    [Fact]
    public async Task SessionPermissionMode_LeadAndAssistant_PersistsValidatesAndRejectsInvalid()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;

        var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "PermArea" }, PergaJsonDefaults.ApiOptions);
        areaResp.EnsureSuccessStatusCode();
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions))!;

        var trackResp = await client.PostAsJsonAsync("/api/v1/tracks", new
        {
            areaId = area.Id,
            title = "Permission Mode Track",
            provider = "codex"
        }, PergaJsonDefaults.ApiOptions);
        trackResp.EnsureSuccessStatusCode();
        using var snapDoc = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync());
        var trackEl = snapDoc.RootElement.GetProperty("track");
        var trackId = trackEl.GetProperty("id").GetString()!;
        var leadSessionId = trackEl.GetProperty("leadSessionId").GetString()!;

        using var retiredAlias = await client.PatchAsJsonAsync(
            $"/api/v1/sessions/{leadSessionId}", new { permission_mode = "full" }, PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.BadRequest, retiredAlias.StatusCode);

        // 1. Lead session defaults to provider's default ("never" for codex) and can switch to "full".
        using var putLeadFull = await client.PatchAsJsonAsync(
            $"/api/v1/sessions/{leadSessionId}",
            new { permissionMode = "full" },
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.OK, putLeadFull.StatusCode);
        var updatedLead = (await putLeadFull.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("full", updatedLead.PermissionMode);

        // 2. Assistant session created with explicit permissionMode = "acceptEdits" for claude.
        using var createSubReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tracks/{trackId}/sessions")
        {
            Content = JsonContent.Create(new { title = "Claude Helper", provider = "claude", permissionMode = "acceptEdits" })
        };
        createSubReq.Headers.Add("Idempotency-Key", "idem-perm-sub-1");
        using var createSubResp = await client.SendAsync(createSubReq);
        Assert.Equal(HttpStatusCode.Created, createSubResp.StatusCode);
        var createdSub = (await createSubResp.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("acceptEdits", createdSub.Session.PermissionMode);

        // 3. Switch Assistant session to "bypassPermissions" and verify persistence in GET /sessions.
        using var putSubBypass = await client.PatchAsJsonAsync(
            $"/api/v1/sessions/{createdSub.Session.Id}",
            new { permissionMode = "bypassPermissions" },
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.OK, putSubBypass.StatusCode);
        var updatedSub = (await putSubBypass.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("bypassPermissions", updatedSub.PermissionMode);

        var sessions = (await client.GetFromJsonAsync<List<SessionSummaryDto>>(
            $"/api/v1/tracks/{trackId}/sessions", PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("full", sessions.Single(s => s.Session.Id == leadSessionId).Session.PermissionMode);
        Assert.Equal("bypassPermissions", sessions.Single(s => s.Session.Id == createdSub.Session.Id).Session.PermissionMode);

        // 4. Unsupported mode for provider returns 400.
        using var putInvalid = await client.PatchAsJsonAsync(
            $"/api/v1/sessions/{leadSessionId}",
            new { permissionMode = "bypassPermissions" },
            PergaJsonDefaults.ApiOptions);
        Assert.Equal(HttpStatusCode.BadRequest, putInvalid.StatusCode);

        // 5. Reusing Idempotency-Key with a different permissionMode returns 409 Conflict.
        using var conflictSubReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tracks/{trackId}/sessions")
        {
            Content = JsonContent.Create(new { title = "Claude Helper", provider = "claude", permissionMode = "plan" })
        };
        conflictSubReq.Headers.Add("Idempotency-Key", "idem-perm-sub-1");
        using var conflictSubResp = await client.SendAsync(conflictSubReq);
        Assert.Equal(HttpStatusCode.Conflict, conflictSubResp.StatusCode);
    }
    private static async Task<HttpResponseMessage> SendMessageAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

}
