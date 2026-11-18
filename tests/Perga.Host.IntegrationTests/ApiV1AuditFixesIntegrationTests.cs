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

namespace Perga.Host.IntegrationTests;

public sealed class ApiV1AuditFixesIntegrationTests
{
    [Fact]
    public async Task Bootstrap_ScopeWhoami_Refresh_AndPersistentDatabaseId_WorkEndToEnd()
    {
        var sharedDataDir = Directory.CreateTempSubdirectory("perga-boot-dbid-").FullName;
        try
        {
            string firstDatabaseId;
            string firstInstanceId;

            await using (var cluster1 = new PergaHostTestCluster(explicitDataDir: sharedDataDir))
            {
                // 1. ?scope=whoami skips provider probing completely and returns providers: []
                using var whoamiResp = await cluster1.Client.GetAsync("/api/v1/bootstrap?scope=whoami");
                Assert.Equal(HttpStatusCode.OK, whoamiResp.StatusCode);
                using var whoamiDoc = JsonDocument.Parse(await whoamiResp.Content.ReadAsStringAsync());
                Assert.Equal(0, whoamiDoc.RootElement.GetProperty("providers").GetArrayLength());
                Assert.Equal(0, cluster1.AgentRuntime.ProbeCount);

                firstDatabaseId = whoamiDoc.RootElement.GetProperty("databaseId").GetString()!;
                firstInstanceId = whoamiDoc.RootElement.GetProperty("dbInstanceId").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(firstDatabaseId));
                Assert.False(string.IsNullOrWhiteSpace(firstInstanceId));
                Assert.False(whoamiDoc.RootElement.GetProperty("authRequired").GetBoolean());

                // 2. Full /bootstrap probes once and caches within 60s TTL
                using var boot1 = await cluster1.Client.GetAsync("/api/v1/bootstrap");
                Assert.Equal(HttpStatusCode.OK, boot1.StatusCode);
                Assert.Equal(1, cluster1.AgentRuntime.ProbeCount);

                using var boot2 = await cluster1.Client.GetAsync("/api/v1/bootstrap");
                Assert.Equal(HttpStatusCode.OK, boot2.StatusCode);
                Assert.Equal(1, cluster1.AgentRuntime.ProbeCount); // Served from 60s cache!

                // 3. ?refresh=true bypasses cache and invokes ProbeProvidersAsync(forceRefresh: true, ct)
                using var bootRefresh = await cluster1.Client.GetAsync("/api/v1/bootstrap?refresh=true");
                Assert.Equal(HttpStatusCode.OK, bootRefresh.StatusCode);
                Assert.Equal(2, cluster1.AgentRuntime.ProbeCount);
                Assert.Equal(1, cluster1.AgentRuntime.ForceRefreshProbeCount);
            }

            // 4. Reopening SQLite on same DataDir preserves exact same databaseId!
            await using (var cluster2 = new PergaHostTestCluster(explicitDataDir: sharedDataDir))
            {
                using var bootReopen = await cluster2.Client.GetAsync("/api/v1/bootstrap?scope=whoami");
                Assert.Equal(HttpStatusCode.OK, bootReopen.StatusCode);
                using var reopenDoc = JsonDocument.Parse(await bootReopen.Content.ReadAsStringAsync());
                Assert.Equal(firstDatabaseId, reopenDoc.RootElement.GetProperty("databaseId").GetString());
            }
        }
        finally
        {
            try { Directory.Delete(sharedDataDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HostListDir_BinaryFiles_RawFilesSecurity_AndDetailedVcsDiff_WorkForGitAndJj()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;

        // 1. GET /api/v1/fs/listdir default ($HOME), explicit sorted dir, 404 missing, 400 file
        using var homeResp = await client.GetAsync("/api/v1/fs/listdir");
        Assert.Equal(HttpStatusCode.OK, homeResp.StatusCode);
        using var homeDoc = JsonDocument.Parse(await homeResp.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(homeDoc.RootElement.GetProperty("path").GetString()));

        var browseRoot = Path.Combine(cluster.DataDir, "browse-test");
        Directory.CreateDirectory(Path.Combine(browseRoot, "zeta-dir"));
        Directory.CreateDirectory(Path.Combine(browseRoot, "Alpha-Dir"));
        await File.WriteAllTextAsync(Path.Combine(browseRoot, "b-file.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(browseRoot, "A-File.txt"), "world");
        var symlinkTarget = Path.Combine(browseRoot, "b-file.txt");
        File.CreateSymbolicLink(Path.Combine(browseRoot, "link-file.txt"), symlinkTarget);

        using var listResp = await client.GetAsync($"/api/v1/fs/listdir?path={Uri.EscapeDataString(browseRoot)}");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        Assert.Equal(Path.GetFullPath(browseRoot), listDoc.RootElement.GetProperty("path").GetString());
        Assert.Equal(Directory.GetParent(Path.GetFullPath(browseRoot))?.FullName, listDoc.RootElement.GetProperty("parent").GetString());

        var entries = listDoc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(5, entries.Length);
        // Directories first (case-insensitive), then files (case-insensitive)
        Assert.Equal("Alpha-Dir", entries[0].GetProperty("name").GetString());
        Assert.True(entries[0].GetProperty("isDir").GetBoolean());
        Assert.Equal("zeta-dir", entries[1].GetProperty("name").GetString());
        Assert.True(entries[1].GetProperty("isDir").GetBoolean());
        Assert.Equal("A-File.txt", entries[2].GetProperty("name").GetString());
        Assert.False(entries[2].GetProperty("isDir").GetBoolean());
        Assert.Equal("b-file.txt", entries[3].GetProperty("name").GetString());
        Assert.Equal("link-file.txt", entries[4].GetProperty("name").GetString());
        Assert.True(entries[4].GetProperty("isSymlink").GetBoolean());

        using var missingDirResp = await client.GetAsync($"/api/v1/fs/listdir?path={Uri.EscapeDataString(Path.Combine(browseRoot, "does-not-exist"))}");
        Assert.Equal(HttpStatusCode.NotFound, missingDirResp.StatusCode);

        using var fileAsDirResp = await client.GetAsync($"/api/v1/fs/listdir?path={Uri.EscapeDataString(symlinkTarget)}");
        Assert.Equal(HttpStatusCode.BadRequest, fileAsDirResp.StatusCode);

        // 1b. SHELL-04: listing /tmp (which contains root-only systemd-private-* entries) and a readable parent
        //     containing a chmod-000 child MUST return 200 OK, while listing the chmod-000 dir itself returns 403!
        using var tmpResp = await client.GetAsync("/api/v1/fs/listdir?path=%2Ftmp");
        var tmpBody = await tmpResp.Content.ReadAsStringAsync();
        Assert.True(tmpResp.StatusCode == HttpStatusCode.OK, $"Expected 200 OK for /tmp, got {tmpResp.StatusCode}: {tmpBody}");

        if (!OperatingSystem.IsWindows())
        {
            using var rootResp = await client.GetAsync("/api/v1/fs/listdir?path=%2F");
            Assert.Equal(HttpStatusCode.OK, rootResp.StatusCode);
        }

        if (!OperatingSystem.IsWindows())
        {
            var lockedChild = Path.Combine(browseRoot, "locked-child");
            Directory.CreateDirectory(lockedChild);
            File.SetUnixFileMode(lockedChild, UnixFileMode.None);
            try
            {
                using var parentWithLockedChildResp = await client.GetAsync($"/api/v1/fs/listdir?path={Uri.EscapeDataString(browseRoot)}");
                Assert.Equal(HttpStatusCode.OK, parentWithLockedChildResp.StatusCode);

                // Root inside containers bypasses UnixFileMode.None; non-root users get 403 Forbidden on the locked target itself.
                using var lockedSelfResp = await client.GetAsync($"/api/v1/fs/listdir?path={Uri.EscapeDataString(lockedChild)}");
                if (Environment.IsPrivilegedProcess)
                {
                    Assert.Equal(HttpStatusCode.OK, lockedSelfResp.StatusCode);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.Forbidden, lockedSelfResp.StatusCode);
                }
            }
            finally
            {
                File.SetUnixFileMode(lockedChild, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // 2. Initialize a Git repo workspace and test /files (binary detection), /files/raw (headers + containment), and /vcs-diff
        var gitRepo = Path.Combine(cluster.DataDir, "git-diff-ws");
        Directory.CreateDirectory(gitRepo);
        await RunCmdAsync(gitRepo, "git", "init", "-q");
        await RunCmdAsync(gitRepo, "git", "config", "user.email", "test@perga.local");
        await RunCmdAsync(gitRepo, "git", "config", "user.name", "Perga Test");

        await File.WriteAllTextAsync(Path.Combine(gitRepo, "modified.txt"), "line 1\nline 2\n");
        await File.WriteAllTextAsync(Path.Combine(gitRepo, "deleted.txt"), "old content\n");
        await RunCmdAsync(gitRepo, "git", "add", ".");
        await RunCmdAsync(gitRepo, "git", "commit", "-q", "-m", "initial");

        // Working tree edits: modify, delete, untracked text, untracked binary PNG, HTML, and external symlink!
        await File.WriteAllTextAsync(Path.Combine(gitRepo, "modified.txt"), "line 1\nline 2 updated\n");
        File.Delete(Path.Combine(gitRepo, "deleted.txt"));
        await File.WriteAllTextAsync(Path.Combine(gitRepo, "untracked.txt"), "brand new file\n");
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0xFF };
        await File.WriteAllBytesAsync(Path.Combine(gitRepo, "logo.png"), pngBytes);
        await File.WriteAllTextAsync(Path.Combine(gitRepo, "xss.html"), "<script>alert(1)</script>");

        var secretOutside = Path.Combine(cluster.DataDir, "outside-secret.txt");
        await File.WriteAllTextAsync(secretOutside, "TOP_SECRET");
        File.CreateSymbolicLink(Path.Combine(gitRepo, "escape-link.txt"), secretOutside);

        using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "VCS Area", defaultCwd = gitRepo });
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions))!;

        using var trackResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new { areaId = area.Id, title = "Git Diff Track", cwd = gitRepo, workspaceDriver = "direct" });
        using var trackDoc = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync());
        var trackId = trackDoc.RootElement.GetProperty("track").GetProperty("id").GetString()!;

        // Binary vs Text file check on GET /tracks/{trackId}/files
        using var binFileResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files?path=logo.png");
        Assert.Equal(HttpStatusCode.OK, binFileResp.StatusCode);
        using var binFileDoc = JsonDocument.Parse(await binFileResp.Content.ReadAsStringAsync());
        Assert.True(binFileDoc.RootElement.GetProperty("isBinary").GetBoolean());
        Assert.Equal(JsonValueKind.Null, binFileDoc.RootElement.GetProperty("content").ValueKind);

        using var txtFileResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files?path=modified.txt");
        Assert.Equal(HttpStatusCode.OK, txtFileResp.StatusCode);
        using var txtFileDoc = JsonDocument.Parse(await txtFileResp.Content.ReadAsStringAsync());
        Assert.False(txtFileDoc.RootElement.GetProperty("isBinary").GetBoolean());
        Assert.Contains("line 2 updated", txtFileDoc.RootElement.GetProperty("content").GetString());

        // Raw file streaming + CSP sandbox + path traversal & external symlink defense!
        using var rawPngResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files/raw?path=logo.png");
        Assert.Equal(HttpStatusCode.OK, rawPngResp.StatusCode);
        Assert.Equal("image/png", rawPngResp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", rawPngResp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(pngBytes, await rawPngResp.Content.ReadAsByteArrayAsync());

        using var rawHtmlResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files/raw?path=xss.html");
        Assert.Equal(HttpStatusCode.OK, rawHtmlResp.StatusCode);
        Assert.Equal("nosniff", rawHtmlResp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("sandbox", rawHtmlResp.Headers.GetValues("Content-Security-Policy").Single());

        using var escapeDotDotResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files/raw?path={Uri.EscapeDataString("../outside-secret.txt")}");
        Assert.Equal(HttpStatusCode.Forbidden, escapeDotDotResp.StatusCode);

        using var escapeSymlinkResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files/raw?path=escape-link.txt");
        Assert.Equal(HttpStatusCode.Forbidden, escapeSymlinkResp.StatusCode);

        using var rawDirResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files/raw");
        Assert.Equal(HttpStatusCode.BadRequest, rawDirResp.StatusCode);

        // Detailed VCS diff on Git repo (+ ?path= filter)
        using var diffResp = await client.GetAsync($"/api/v1/tracks/{trackId}/vcs-diff");
        var diffRaw = await diffResp.Content.ReadAsStringAsync();
        Assert.True(diffResp.StatusCode == HttpStatusCode.OK, $"Expected 200 OK from /vcs-diff, got {diffResp.StatusCode}: {diffRaw}");
        using var diffDoc = JsonDocument.Parse(diffRaw);
        var filesMap = diffDoc.RootElement.GetProperty("files")
            .EnumerateArray()
            .ToDictionary(e => e.GetProperty("path").GetString()!);

        Assert.Equal("modified", filesMap["modified.txt"].GetProperty("status").GetString());
        Assert.Equal("line 1\nline 2\n", filesMap["modified.txt"].GetProperty("headText").GetString());
        Assert.Equal("line 1\nline 2 updated\n", filesMap["modified.txt"].GetProperty("workingText").GetString());
        Assert.Contains("-line 2", filesMap["modified.txt"].GetProperty("patch").GetString());
        Assert.Contains("+line 2 updated", filesMap["modified.txt"].GetProperty("patch").GetString());

        Assert.Equal("deleted", filesMap["deleted.txt"].GetProperty("status").GetString());
        Assert.Equal("old content\n", filesMap["deleted.txt"].GetProperty("headText").GetString());
        Assert.Equal(JsonValueKind.Null, filesMap["deleted.txt"].GetProperty("workingText").ValueKind);

        Assert.Equal("untracked", filesMap["untracked.txt"].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, filesMap["untracked.txt"].GetProperty("headText").ValueKind);
        Assert.Equal("brand new file\n", filesMap["untracked.txt"].GetProperty("workingText").GetString());

        Assert.True(filesMap["logo.png"].GetProperty("isBinary").GetBoolean());
        Assert.Equal(JsonValueKind.Null, filesMap["logo.png"].GetProperty("headText").ValueKind);
        Assert.Equal(JsonValueKind.Null, filesMap["logo.png"].GetProperty("workingText").ValueKind);

        using var filteredDiffResp = await client.GetAsync($"/api/v1/tracks/{trackId}/vcs-diff?path=modified.txt");
        Assert.Equal(HttpStatusCode.OK, filteredDiffResp.StatusCode);
        using var filteredDoc = JsonDocument.Parse(await filteredDiffResp.Content.ReadAsStringAsync());
        var singleFiltered = Assert.Single(filteredDoc.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal("modified.txt", singleFiltered.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("dev", "vcs-workspace")]
    [InlineData(null, "direct")]
    public async Task BlankWorkspaceDoesNotUseTheRepositoryContainingTheDataDirectory(string? templateId, string driver)
    {
        var enclosingRepo = Directory.CreateTempSubdirectory("perga-enclosing-repo-");
        try
        {
            var root = enclosingRepo.FullName;
            await RunCmdAsync(root, "git", "init", "-q");
            await RunCmdAsync(root, "git", "config", "user.email", "test@perga.local");
            await RunCmdAsync(root, "git", "config", "user.name", "Perga Test");
            await File.WriteAllTextAsync(Path.Combine(root, "parent-only.txt"), "Parent repository");
            await RunCmdAsync(root, "git", "add", "parent-only.txt");
            await RunCmdAsync(root, "git", "commit", "-q", "-m", "initial");
            await using var cluster = new PergaHostTestCluster(Path.Combine(root, "perga-data"));
            using var areaResponse = await cluster.Client.PostAsJsonAsync("/api/v1/areas", new { name = "Blank area" });
            var area = (await areaResponse.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions))!;
            using var trackResponse = await cluster.Client.PostAsJsonAsync("/api/v1/tracks",
                new { areaId = area.Id, templateId });
            Assert.Equal(HttpStatusCode.Created, trackResponse.StatusCode);

            using var snapshot = JsonDocument.Parse(await trackResponse.Content.ReadAsStringAsync());
            var track = snapshot.RootElement.GetProperty("track");
            Assert.Equal(driver, track.GetProperty("workspaceDriver").GetString());
            var id = track.GetProperty("id").GetString()!;
            await cluster.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(id);
            using var ready = JsonDocument.Parse(await cluster.Client.GetStringAsync($"/api/v1/tracks/{id}"));
            var cwd = ready.RootElement.GetProperty("track").GetProperty("cwd").GetString()!;
            Assert.Equal(driver == "vcs-workspace", Directory.Exists(Path.Combine(cwd, ".git")));
            Assert.False(File.Exists(Path.Combine(cwd, "parent-only.txt")));
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
            File.Delete(Path.Combine(cwd, ".git", "HEAD"));
            await File.AppendAllTextAsync(Path.Combine(root, "parent-only.txt"), " changed");
            using var diff = JsonDocument.Parse(await cluster.Client.GetStringAsync($"/api/v1/tracks/{id}/vcs-diff"));
            Assert.Empty(diff.RootElement.GetProperty("changedFiles").EnumerateArray());
        }
        finally
        {
            enclosingRepo.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BlankCwdIsolation_IdempotencyHeaders_NonBlockingFirstMessage_PatchSession_AndBeforeIdPagination_WorkEndToEnd()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;

        // 1. POST /api/v1/areas with Idempotency-Key + blank DefaultCwd
        using var areaReq1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/areas")
        {
            Content = JsonContent.Create(new { name = "Blank Area" })
        };
        areaReq1.Headers.Add("Idempotency-Key", "idem-area-1");
        using var areaResp1 = await client.SendAsync(areaReq1);
        Assert.Equal(HttpStatusCode.Created, areaResp1.StatusCode);
        var area1 = (await areaResp1.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions))!;

        using var areaReq2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/areas")
        {
            Content = JsonContent.Create(new { name = "Blank Area" })
        };
        areaReq2.Headers.Add("Idempotency-Key", "idem-area-1");
        using var areaResp2 = await client.SendAsync(areaReq2);
        var area2 = (await areaResp2.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal(area1.Id, area2.Id);

        // 2. Gate the firstMessage LLM turn so we prove POST /api/v1/tracks returns 201 IMMEDIATELY without blocking on LLM!
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.AgentRuntime.SetTurnHandler((_, input, ct) => StreamBlockedFirstMessage(turnStarted, releaseTurn.Task, input, ct));

        using var trackReq1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tracks")
        {
            Content = JsonContent.Create(new
            {
                areaId = area1.Id,
                title = "Isolated Blank Track",
                templateId = "dev",
                firstMessage = "Kick off async first turn"
            })
        };
        trackReq1.Headers.Add("Idempotency-Key", "idem-track-1");

        var createSw = Stopwatch.StartNew();
        using var trackResp1 = await client.SendAsync(trackReq1);
        createSw.Stop();
        Assert.Equal(HttpStatusCode.Created, trackResp1.StatusCode);

        using var trackSnap1 = JsonDocument.Parse(await trackResp1.Content.ReadAsStringAsync());
        var trackDto = trackSnap1.RootElement.GetProperty("track");
        var trackId = trackDto.GetProperty("id").GetString()!;
        Assert.Equal("vcs-workspace", trackDto.GetProperty("workspaceDriver").GetString());
        var leadSessionId = trackDto.GetProperty("leadSessionId").GetString()!;

        // Idempotent retry of POST /api/v1/tracks returns exact same trackId!
        using var trackReq2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tracks")
        {
            Content = JsonContent.Create(new
            {
                areaId = area1.Id,
                title = "Isolated Blank Track",
                templateId = "dev",
                firstMessage = "Kick off async first turn"
            })
        };
        trackReq2.Headers.Add("Idempotency-Key", "idem-track-1");
        using var trackResp2 = await client.SendAsync(trackReq2);
        using var trackSnap2 = JsonDocument.Parse(await trackResp2.Content.ReadAsStringAsync());
        Assert.Equal(trackId, trackSnap2.RootElement.GetProperty("track").GetProperty("id").GetString());

        // Wait for background firstMessage turn to enter StreamTurnAsync -> verify 409 Conflict on PATCH model while running!
        await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var readySnapshot = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/tracks/{trackId}"));
        var trackCwd = readySnapshot.RootElement.GetProperty("track").GetProperty("cwd").GetString()!;
        // Isolated blank workspace MUST be inside WorkspacesRootDirectory, NEVER Directory.GetCurrentDirectory()!
        Assert.NotEqual(Path.GetFullPath(Directory.GetCurrentDirectory()), Path.GetFullPath(trackCwd));
        Assert.StartsWith(Path.GetFullPath(Path.Combine(cluster.DataDir, "workspaces")), Path.GetFullPath(trackCwd), StringComparison.Ordinal);
        Assert.True(Directory.Exists(trackCwd));


        using var conflictPatch = await client.PatchAsJsonAsync(
            $"/api/v1/sessions/{leadSessionId}",
            new { provider = "claude", model = "claude-sonnet-4-6" });
        Assert.Equal(HttpStatusCode.Conflict, conflictPatch.StatusCode);

        // Release firstMessage turn and wait for idle
        releaseTurn.TrySetResult();
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId);

        // Restore default fast turn handler
        cluster.AgentRuntime.SetTurnHandler((_, input, _) => StreamQuickTurn(input));

        // 3. PATCH /api/v1/sessions/{sessionId} when idle: update title, provider, model, then explicit model: null!
        using var patchSesResp = await client.PatchAsJsonAsync(
            $"/api/v1/sessions/{leadSessionId}",
            new { title = "Lead (Claude)", provider = "claude", model = "claude-sonnet-4-6" });
        Assert.Equal(HttpStatusCode.OK, patchSesResp.StatusCode);
        var patchedSes = (await patchSesResp.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("Lead (Claude)", patchedSes.Title);
        Assert.Equal("claude", patchedSes.Provider);
        Assert.Equal("claude-sonnet-4-6", patchedSes.Model);
        Assert.Null(patchedSes.NativeThreadId); // Reset when switching provider!

        using var clearModelReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/sessions/{leadSessionId}")
        {
            Content = new StringContent("""{"model":null}""", Encoding.UTF8, "application/json")
        };
        using var clearModelResp = await client.SendAsync(clearModelReq);
        Assert.Equal(HttpStatusCode.OK, clearModelResp.StatusCode);
        var clearedSes = (await clearModelResp.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions))!;
        Assert.Null(clearedSes.Model);

        // 4. POST /api/v1/tracks/{trackId}/sessions with Idempotency-Key header returns SessionCreationReceipt (#7)
        using var subReq1 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tracks/{trackId}/sessions")
        {
            Content = JsonContent.Create(new { title = "Explorer", provider = "antigravity" })
        };
        subReq1.Headers.Add("Idempotency-Key", "idem-sub-1");
        using var subResp1 = await client.SendAsync(subReq1);
        Assert.Equal(HttpStatusCode.Created, subResp1.StatusCode);
        var sub1 = (await subResp1.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("acp:antigravity", sub1.Session.Provider);

        using var subReq2 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tracks/{trackId}/sessions")
        {
            Content = JsonContent.Create(new { title = "Explorer", provider = "antigravity" })
        };
        subReq2.Headers.Add("Idempotency-Key", "idem-sub-1");
        using var subResp2 = await client.SendAsync(subReq2);
        Assert.Equal(HttpStatusCode.OK, subResp2.StatusCode);
        var sub2 = (await subResp2.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal(sub1.Session.Id, sub2.Session.Id);

        // 5. Generate additional durable items (202 Accepted SessionInputReceipt) and test ?beforeId=...&limit=...
        using var msg2Req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
        {
            Content = JsonContent.Create(new { content = "Turn 2" })
        };
        msg2Req.Headers.Add("Idempotency-Key", "idem-msg-2");
        using var msg2 = await client.SendAsync(msg2Req);
        Assert.Equal(HttpStatusCode.Accepted, msg2.StatusCode);
        var receipt2 = (await msg2.Content.ReadFromJsonAsync<SessionInputReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.False(string.IsNullOrWhiteSpace(receipt2.InputId));
        await kernel.WaitForTrackIdleAsync(trackId);

        using var msg3Req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
        {
            Content = JsonContent.Create(new { content = "Turn 3" })
        };
        msg3Req.Headers.Add("Idempotency-Key", "idem-msg-3");
        using var msg3 = await client.SendAsync(msg3Req);
        Assert.Equal(HttpStatusCode.Accepted, msg3.StatusCode);
        await kernel.WaitForTrackIdleAsync(trackId);

        // Also verify durable turn conclusive error persists `turn_outcome` item in /items!
        cluster.AgentRuntime.SetTurnHandler((_, _, _) => StreamConclusiveDurableFailure());
        using var msgFailReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
        {
            Content = JsonContent.Create(new { content = "Turn 4 fail" })
        };
        msgFailReq.Headers.Add("Idempotency-Key", "idem-msg-4-fail");
        using var msgFail = await client.SendAsync(msgFailReq);
        Assert.Equal(HttpStatusCode.Accepted, msgFail.StatusCode);
        await kernel.WaitForTrackIdleAsync(trackId);

        using var fullPageResp = await client.GetAsync($"/api/v1/sessions/{leadSessionId}/items?limit=100");
        using var fullPageDoc = JsonDocument.Parse(await fullPageResp.Content.ReadAsStringAsync());
        var allItems = fullPageDoc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(allItems, i => i.GetProperty("kind").GetString() == "turn_outcome");
        var allIds = allItems
            .Select(i => i.GetProperty("id").GetInt64())
            .ToArray();
        Assert.True(allIds.Length >= 6);

        var anchorBeforeId = allIds[^1];
        using var beforePageResp = await client.GetAsync($"/api/v1/sessions/{leadSessionId}/items?beforeId={anchorBeforeId}&limit=3");
        Assert.Equal(HttpStatusCode.OK, beforePageResp.StatusCode);
        using var beforePageDoc = JsonDocument.Parse(await beforePageResp.Content.ReadAsStringAsync());
        Assert.True(beforePageDoc.RootElement.GetProperty("hasMoreBefore").GetBoolean());
        var pageIds = beforePageDoc.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("id").GetInt64())
            .ToArray();
        Assert.Equal(allIds[^4..^1], pageIds); // Latest 3 items strictly < anchorBeforeId, sorted ASC!
    }

    [Fact]
    public async Task TaskGovernanceEndpoints_PermanentTerminalDelete_Overlays_AndLifecycleEvents_WorkEndToEnd()
    {
        await using var cluster = new PergaHostTestCluster();
        var client = cluster.Client;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var wsDir = Path.Combine(cluster.DataDir, "gov-ws");
        Directory.CreateDirectory(wsDir);

        // Connect live WebSocket with since=0 -> MUST immediately receive _replay_complete with zero historical replay!
        using var ws = await cluster.ConnectEventsWebSocketAsync("?since=0", cts.Token);
        using var initFrame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(ws, cts.Token);
        Assert.Equal("_replay_complete", initFrame.RootElement.GetProperty("kind").GetString());

        using var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new { name = "Ops Area", defaultCwd = wsDir }, cts.Token);
        var area = (await areaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions, cts.Token))!;

        using var patchAreaResp = await client.PatchAsJsonAsync($"/api/v1/areas/{area.Id}", new { name = "Ops Area Renamed" }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, patchAreaResp.StatusCode);

        using var trackResp = await client.PostAsJsonAsync(
            "/api/v1/tracks",
            new { areaId = area.Id, title = "Gov Track", cwd = wsDir, workspaceDriver = "direct" },
            cts.Token);
        using var trackDoc = JsonDocument.Parse(await trackResp.Content.ReadAsStringAsync(cts.Token));
        var trackId = trackDoc.RootElement.GetProperty("track").GetProperty("id").GetString()!;

        // 1. Add task blocks (one failing command task + one pending task) and verify human governance REST routes
        using var reportResp = await client.GetAsync($"/api/v1/tracks/{trackId}/report", cts.Token);
        using var reportDoc = JsonDocument.Parse(await reportResp.Content.ReadAsStringAsync(cts.Token));
        var docRev = reportDoc.RootElement.GetProperty("docRev").GetInt32();

        using var putFailTask = await client.PutAsJsonAsync(
            $"/api/v1/tracks/{trackId}/report/blocks/b_fail_gov",
            new
            {
                docRev,
                knownBlockRev = 0,
                kind = "task",
                payload = new
                {
                    taskKey = "failing-check",
                    kind = "terminal",
                    ready = true,
                    command = "exit 2",
                    maxRetries = 1
                }
            },
            cts.Token);
        Assert.Equal(HttpStatusCode.OK, putFailTask.StatusCode);

        using var putPendingTask = await client.PutAsJsonAsync(
            $"/api/v1/tracks/{trackId}/report/blocks/b_cancel_gov",
            new
            {
                docRev = docRev + 1,
                knownBlockRev = 0,
                kind = "task",
                payload = new
                {
                    taskKey = "cancel-me",
                    kind = "terminal",
                    ready = false,
                    command = "echo noop"
                }
            },
            cts.Token);
        Assert.Equal(HttpStatusCode.OK, putPendingTask.StatusCode);

        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId, cts.Token);

        // Verify GET /api/v1/overlays reflects hasFailedTask == true!
        using var overlaysBefore = await client.GetAsync("/api/v1/overlays", cts.Token);
        var overlaysRaw = await overlaysBefore.Content.ReadAsStringAsync(cts.Token);
        Assert.True(overlaysBefore.StatusCode == HttpStatusCode.OK, $"Expected 200 OK from /overlays, got {overlaysBefore.StatusCode}: {overlaysRaw}");
        using var overlaysDoc1 = JsonDocument.Parse(overlaysRaw);
        var trackOverlay1 = overlaysDoc1.RootElement.GetProperty("tracks")
            .EnumerateArray()
            .Single(t => t.GetProperty("trackId").GetString() == trackId);
        Assert.True(trackOverlay1.GetProperty("hasFailedTask").GetBoolean());

        // Human governance REST endpoints: /cancel, /accept, /reject!
        using var cancelResp = await client.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/tasks/cancel-me/cancel",
            new { reason = "Skipped by operator" },
            cts.Token);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
        var canceledTask = (await cancelResp.Content.ReadFromJsonAsync<TaskDto>(PergaJsonDefaults.ApiOptions, cts.Token))!;
        Assert.Equal(TaskRunStatus.Canceled, canceledTask.Status);

        using var acceptResp = await client.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/tasks/failing-check/accept",
            new { note = "Accepted manually" },
            cts.Token);
        Assert.Equal(HttpStatusCode.OK, acceptResp.StatusCode);
        var acceptedTask = (await acceptResp.Content.ReadFromJsonAsync<TaskDto>(PergaJsonDefaults.ApiOptions, cts.Token))!;
        Assert.Equal(SemanticVerdict.Accepted, acceptedTask.SemanticVerdict);
        Assert.Equal("Accepted manually", acceptedTask.SemanticNote);

        using var rejectResp = await client.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/tasks/failing-check/reject",
            new { note = "Re-marked failed", reQueue = false },
            cts.Token);
        Assert.Equal(HttpStatusCode.OK, rejectResp.StatusCode);
        var rejectedTask = (await rejectResp.Content.ReadFromJsonAsync<TaskDto>(PergaJsonDefaults.ApiOptions, cts.Token))!;
        Assert.Equal(TaskRunStatus.Failed, rejectedTask.Status);
        Assert.Equal(SemanticVerdict.Rejected, rejectedTask.SemanticVerdict);

        // 2. Open terminal and DELETE permanently -> GET /terminals must be empty!
        using var openTermResp = await client.PostAsJsonAsync(
            $"/api/v1/tracks/{trackId}/terminals",
            new { title = "Temp Shell" },
            cts.Token);
        Assert.Equal(HttpStatusCode.Created, openTermResp.StatusCode);
        var term = (await openTermResp.Content.ReadFromJsonAsync<TerminalDto>(PergaJsonDefaults.ApiOptions, cts.Token))!;

        using var delTermResp = await client.DeleteAsync($"/api/v1/tracks/{trackId}/terminals/{term.Id}", cts.Token);
        Assert.Equal(HttpStatusCode.NoContent, delTermResp.StatusCode);

        using var listTermsResp = await client.GetAsync($"/api/v1/tracks/{trackId}/terminals", cts.Token);
        var termsAfterDelete = (await listTermsResp.Content.ReadFromJsonAsync<List<TerminalDto>>(PergaJsonDefaults.ApiOptions, cts.Token))!;
        Assert.Empty(termsAfterDelete);

        // 3. Delete track & area and verify area.updated, terminal.changed(deleted:true), track.deleted, area.deleted arrived on WS!
        using var delTrackResp = await client.DeleteAsync($"/api/v1/tracks/{trackId}", cts.Token);
        Assert.Equal(HttpStatusCode.NoContent, delTrackResp.StatusCode);

        using var delAreaResp = await client.DeleteAsync($"/api/v1/areas/{area.Id}", cts.Token);
        Assert.Equal(HttpStatusCode.NoContent, delAreaResp.StatusCode);

        var observedKinds = new HashSet<string>(StringComparer.Ordinal);
        var sawTerminalDeletedPayload = false;
        while (!observedKinds.Contains("area.deleted"))
        {
            using var evtFrame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(ws, cts.Token);
            var kind = evtFrame.RootElement.GetProperty("kind").GetString()!;
            observedKinds.Add(kind);
            if (kind == "terminal.changed" &&
                evtFrame.RootElement.TryGetProperty("payload", out var termPayload) &&
                termPayload.TryGetProperty("deleted", out var delProp) &&
                delProp.ValueKind == JsonValueKind.True)
            {
                sawTerminalDeletedPayload = true;
            }
        }

        Assert.Contains("area.updated", observedKinds);
        Assert.Contains("track.deleted", observedKinds);
        Assert.Contains("area.deleted", observedKinds);
        Assert.True(sawTerminalDeletedPayload);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamBlockedFirstMessage(
        TaskCompletionSource started,
        Task release,
        TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ThreadBound($"thr_{Guid.NewGuid():N}");
        yield return new UnifiedAgentEvent.ThinkingDelta("Starting async first turn...");
        started.TrySetResult();
        await release.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TextDelta($"First response: {input.Prompt}");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(50, 20, 0, 100_000));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamQuickTurn(TurnInput input)
    {
        await Task.Yield();
        yield return new UnifiedAgentEvent.ThinkingDelta("Quick thought");
        yield return new UnifiedAgentEvent.TextDelta($"Echo: {input.Prompt}");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(30, 10, 0, 100_000));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamConclusiveDurableFailure()
    {
        await Task.Yield();
        yield return new UnifiedAgentEvent.TurnError("quota_exhausted", "Provider quota exceeded", IsRetryable: false, IsConclusive: true);
    }

    private static async Task RunCmdAsync(string workingDir, string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} failed ({proc.ExitCode}): {err}");
        }
    }
}
