using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class TrackCreationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ColdAcpModePersistsWithoutOpeningProvider()
    {
        await using var host = new PergaHostTestCluster();
        var areaId = await CreateAreaAsync(host);
        var probes = host.AgentRuntime.ProbeCount;
        using var response = await PostAsync(host, "/api/v1/tracks", "cold-acp-mode",
            new { areaId, provider = "acp:antigravity", permissionMode = "plan" });
        response.EnsureSuccessStatusCode();
        var trackId = (await ReadAsync(response)).GetProperty("track").GetProperty("id").GetString()!;
        Assert.Equal("plan", Assert.Single((await host.Services.GetRequiredService<IPergaKernel>().ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).PermissionMode);
        Assert.Equal(probes, host.AgentRuntime.ProbeCount);
        Assert.Empty(host.AgentRuntime.OpenedSessions);
    }

    [Fact]
    public async Task SessionReadsUseOpenAcpPermissionsWithoutOpeningProvider()
    {
        await using var host = new PergaHostTestCluster();
        host.AgentRuntime.AcpPermissionModes =
            [new("inspect", "Inspect", "Read only", IsDefault: true), new("act", "Act", "Make changes")];
        var areaId = await CreateAreaAsync(host);
        using var response = await PostAsync(host, "/api/v1/tracks", "acp-live-modes",
            new { areaId, provider = "acp:antigravity" });
        var track = (await ReadAsync(response)).GetProperty("track");
        var sessionId = track.GetProperty("leadSessionId").GetString()!;
        var trackId = track.GetProperty("id").GetString()!;
        var path = $"/api/v1/sessions/{sessionId}";

        async Task<SessionItemsPageDto> ReadPageAsync() =>
            (await host.AuthenticatedClient.GetFromJsonAsync<SessionItemsPageDto>(path + "/items", PergaJsonDefaults.ApiOptions))!;

        var cold = await ReadPageAsync();
        Assert.Null(cold.PermissionModes);
        Assert.Empty(host.AgentRuntime.OpenedSessions);

        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        using var first = await PostAsync(host, path + "/messages", "acp-live-input",
            new { content = "Start the conversation." });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        var open = await ReadPageAsync();
        Assert.Equal(host.AgentRuntime.AcpPermissionModes, open.PermissionModes);
        Assert.Single(host.AgentRuntime.OpenedSessions);

        using var update = await host.AuthenticatedClient.PatchAsJsonAsync(path, new { permissionMode = "act" });
        update.EnsureSuccessStatusCode();
        var updated = await ReadPageAsync();
        Assert.Equal("act", updated.Session.PermissionMode);
        Assert.Equal(host.AgentRuntime.AcpPermissionModes, updated.PermissionModes);
        Assert.Single(host.AgentRuntime.OpenedSessions);

        using var changeProvider = await host.AuthenticatedClient.PatchAsJsonAsync(path, new { provider = "codex" });
        changeProvider.EnsureSuccessStatusCode();
        var switched = await ReadPageAsync();
        Assert.Null(switched.PermissionModes);
        Assert.Single(host.AgentRuntime.OpenedSessions);
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.OK)]
    [InlineData("act", "act", HttpStatusCode.OK)]
    [InlineData("unknown", "unknown", HttpStatusCode.OK)]
    [InlineData("   ", null, HttpStatusCode.BadRequest)]
    public async Task TrackCreateUsesRuntimeModesBeforeBootstrap(string? selected, string? expected, HttpStatusCode status)
    {
        await using var host = new PergaHostTestCluster();
        host.AgentRuntime.AcpPermissionModes = [new("inspect", "Inspect", "", "neutral", true), new("act", "Act", "", "warning")];
        var areaId = await CreateAreaAsync(host);
        using var response = await PostAsync(host, "/api/v1/tracks", "acp-mode-create",
            new { areaId, provider = "acp:antigravity", permissionMode = selected });
        if (status == HttpStatusCode.BadRequest) { Assert.Equal(status, response.StatusCode); return; }
        response.EnsureSuccessStatusCode();
        var created = await ReadAsync(response);
        var trackId = created.GetProperty("track").GetProperty("id").GetString()!;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        Assert.Equal(expected, Assert.Single((await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).PermissionMode);
    }

    [Fact]
    public async Task AcceptedFirstInputKeepsModelAndEffortWhileWorkspaceWaits()
    {
        var runner = new GatedSetup();
        await using var host = CreateHost(runner);
        var areaId = await CreateAreaAsync(host);
        using var response = await PostAsync(host, "/api/v1/tracks", "frozen-selection",
            new { areaId, templateId = "gated-setup", provider = "codex", model = "A", reasoningEffort = "low" });
        var created = await ReadAsync(response);
        var trackId = created.GetProperty("track").GetProperty("id").GetString()!;
        var sessionId = created.GetProperty("track").GetProperty("leadSessionId").GetString()!;
        await runner.Entered.Task.WaitAsync(Deadline);
        try
        {
            using var firstMsg = await PostAsync(host, $"/api/v1/sessions/{sessionId}/messages", "frozen-input",
                new { content = "frozen input" });
            Assert.Equal(HttpStatusCode.OK, firstMsg.StatusCode);
            using var update = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}", new { model = "B", reasoningEffort = "high" });
            update.EnsureSuccessStatusCode();
            using var permission = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}", new { permissionMode = "full" });
            permission.EnsureSuccessStatusCode();
            Assert.Empty(host.AgentRuntime.ExecutedTurns);
        }
        finally { runner.Release.TrySetResult(0); }
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        var execution = Assert.Single(host.AgentRuntime.ExecutedTurns);
        Assert.Equal("A", execution.Spec.Model);
        Assert.Equal("low", execution.Spec.ReasoningEffort);
        Assert.Equal("full", execution.Spec.PermissionMode);
        var desired = Assert.Single((await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());
        Assert.Equal("B", desired.Model);
        Assert.Equal("high", desired.ReasoningEffort);
        await kernel.SendSessionMessageAsync(sessionId, new(Content: "next input", RequestKey: "next-selection"), new InteropPrincipal.Human(), default);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        var next = host.AgentRuntime.ExecutedTurns.Last();
        Assert.Equal("B", next.Spec.Model);
        Assert.Equal("high", next.Spec.ReasoningEffort);
    }

    [Fact]
    public async Task WorkspaceOptionsAreReadOnlyAndAnExplicitGitChoiceIsPersistedAndReplayed()
    {
        var process = new WorkspaceProcess();
        var vcs = new VcsService(process, new VcsServiceOptions { AutoColocateJj = true, JjExecutablePath = "jj" });
        await using var host = new PergaHostTestCluster(
            configureExtraServices: services => services.AddSingleton<IVcsService>(vcs));
        var areaId = await CreateAreaAsync(host);
        using var inherited = await host.AuthenticatedClient.GetAsync($"/api/v1/areas/{areaId}/workspace-options");
        var inheritedOptions = await ReadAsync(inherited);
        Assert.Equal("direct", inheritedOptions.GetProperty("defaultId").GetString());
        Assert.Equal(new[] { "direct", "copy" },
            inheritedOptions.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("id").GetString()));
        using var cleared = await host.AuthenticatedClient.GetAsync($"/api/v1/areas/{areaId}/workspace-options?cwd=");
        Assert.Equal("managed", (await ReadAsync(cleared)).GetProperty("defaultId").GetString());
        var source = Path.Combine(host.DataDir, "colocated");
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        await File.WriteAllTextAsync(Path.Combine(source, ".git", "HEAD"), "ref: refs/heads/main\n");
        var optionsPath = $"/api/v1/areas/{areaId}/workspace-options?cwd={Uri.EscapeDataString(source)}";
        using var gitOnly = await host.AuthenticatedClient.GetAsync(optionsPath);
        Assert.Equal("git", (await ReadAsync(gitOnly)).GetProperty("defaultId").GetString());
        Assert.False(Directory.Exists(Path.Combine(source, ".jj")));
        Assert.Empty(process.Calls);

        Directory.CreateDirectory(Path.Combine(source, ".jj", "repo"));
        using var colocated = await host.AuthenticatedClient.GetAsync(optionsPath);
        var options = await ReadAsync(colocated);
        Assert.Equal("jj", options.GetProperty("defaultId").GetString());
        Assert.Equal(new[] { "jj", "git", "direct" },
            options.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("id").GetString()));
        Assert.Empty(process.Calls);

        using var rejected = await PostAsync(host, "/api/v1/tracks", "unavailable-workspace",
            new { areaId, cwd = source, workspaceOptionId = "copy" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var unsupported = await PostAsync(host, "/api/v1/tracks", "overlay-without-launcher",
            new { areaId, cwd = source, workspaceDriver = OverlayWorkspaceDriver.Id });
        Assert.Equal(HttpStatusCode.Conflict, unsupported.StatusCode);
        using var failure = JsonDocument.Parse(await unsupported.Content.ReadAsStringAsync());
        Assert.Equal("precondition_failed", failure.RootElement.GetProperty("code").GetString());
        using var tracks = await host.AuthenticatedClient.GetAsync($"/api/v1/areas/{areaId}/tracks");
        Assert.Empty((await ReadAsync(tracks)).EnumerateArray());

        var body = new { areaId, cwd = source, workspaceOptionId = "git" };
        using var created = await PostAsync(host, "/api/v1/tracks", "chosen-git", body);
        var id = (await ReadAsync(created)).GetProperty("track").GetProperty("id").GetString()!;
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(id).WaitAsync(Deadline);
        using var ready = await host.AuthenticatedClient.GetAsync($"/api/v1/tracks/{id}");
        var spec = (await ReadAsync(ready)).GetProperty("track").GetProperty("workspaceSpec");
        Assert.Equal("GitWorktree", spec.GetProperty("metadata").GetProperty("Kind").GetString());
        using var replay = await PostAsync(host, "/api/v1/tracks", "chosen-git", body);
        Assert.Equal(id, (await ReadAsync(replay)).GetProperty("track").GetProperty("id").GetString());
        var checkout = Assert.Single(process.Calls, call => call.Arguments.First() == "worktree");
        Assert.Equal("git", checkout.FileName);
        Assert.Equal("add", checkout.Arguments[1]);
        Assert.All(process.Calls, call => Assert.Equal("git", call.FileName));

        using var managed = await PostAsync(host, "/api/v1/tracks", "empty-cwd",
            new { areaId, cwd = "", workspaceOptionId = "managed" });
        var managedId = (await ReadAsync(managed)).GetProperty("track").GetProperty("id").GetString()!;
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(managedId).WaitAsync(Deadline);
        using var managedReady = await host.AuthenticatedClient.GetAsync($"/api/v1/tracks/{managedId}");
        var managedSpec = (await ReadAsync(managedReady)).GetProperty("track").GetProperty("workspaceSpec");
        Assert.True(managedSpec.GetProperty("managedEmptyWorkspace").GetBoolean());
    }

    [Fact]
    public async Task PlainDirectoryAllowsExplicitCopyAndRespectsTemplateDriverPriority()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate(
            """
            export default defineTemplate({
              id: "copy-template",
              title: "Copy template",
              workspace: { driver: "vcs-workspace" }
            });
            """);

        var plainDir = Directory.CreateDirectory(Path.Combine(host.DataDir, "plain-source")).FullName;
        await File.WriteAllTextAsync(Path.Combine(plainDir, "notes.txt"), "plain content");
        var areaId = await CreateAreaAsync(host);

        using var defaultOptsResp = await host.AuthenticatedClient.GetAsync(
            $"/api/v1/areas/{areaId}/workspace-options?cwd={Uri.EscapeDataString(plainDir)}");
        var defaultOpts = await ReadAsync(defaultOptsResp);
        Assert.Equal("direct", defaultOpts.GetProperty("defaultId").GetString());
        Assert.Equal(new[] { "direct", "copy" },
            defaultOpts.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("id").GetString()));

        using var templateOptsResp = await host.AuthenticatedClient.GetAsync(
            $"/api/v1/areas/{areaId}/workspace-options?cwd={Uri.EscapeDataString(plainDir)}&templateId=copy-template");
        var templateOpts = await ReadAsync(templateOptsResp);
        Assert.Equal("copy", templateOpts.GetProperty("defaultId").GetString());
        Assert.Equal(new[] { "copy", "direct" },
            templateOpts.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("id").GetString()));

        using var explicitCopyResp = await PostAsync(host, "/api/v1/tracks", "plain-explicit-copy",
            new { areaId, cwd = plainDir, workspaceOptionId = "copy" });
        var explicitCopyId = (await ReadAsync(explicitCopyResp)).GetProperty("track").GetProperty("id").GetString()!;
        await kernel.WaitForTrackIdleAsync(explicitCopyId).WaitAsync(Deadline);
        var explicitCopy = (await kernel.GetTrackAggregateAsync(explicitCopyId)).Track;
        Assert.Equal(VcsWorkspaceDriver.Id, explicitCopy.WorkspaceDriver);
        Assert.NotEqual(plainDir, explicitCopy.Cwd);
        Assert.Equal("PlainDirectory", explicitCopy.WorkspaceSpec.GetProperty("metadata").GetProperty("Kind").GetString());
        Assert.Equal("plain content", await File.ReadAllTextAsync(Path.Combine(explicitCopy.Cwd, "notes.txt")));
    }

    [Fact]
    public async Task PlainDirectoryDoesNotSilentlyCopyWhenHostDefaultsToVcsWorkspace()
    {
        var process = new WorkspaceProcess();
        var vcs = new VcsService(process, new VcsServiceOptions { DisableJj = true });
        await using var host = new PergaHostTestCluster(
            configureHostOptions: options => options.DefaultWorkspaceDriver = VcsWorkspaceDriver.Id,
            configureExtraServices: services => services.AddSingleton<IVcsService>(vcs));
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate(
            """
            export default defineTemplate({
              id: "explicit-vcs",
              title: "Explicit VCS",
              workspace: { driver: "vcs-workspace" }
            });
            """);

        var inheritedPlainDir = Directory.CreateDirectory(Path.Combine(host.DataDir, "inherited-plain")).FullName;
        await File.WriteAllTextAsync(Path.Combine(inheritedPlainDir, "inherited.txt"), "inherited");
        var explicitPlainDir = Directory.CreateDirectory(Path.Combine(host.DataDir, "explicit-plain")).FullName;
        await File.WriteAllTextAsync(Path.Combine(explicitPlainDir, "explicit.txt"), "explicit");
        var gitDir = Directory.CreateDirectory(Path.Combine(host.DataDir, "git-repo")).FullName;
        Directory.CreateDirectory(Path.Combine(gitDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(gitDir, ".git", "HEAD"), "ref: refs/heads/main\n");

        using var areaResp = await PostAsync(host, "/api/v1/areas", "plain-default-area",
            new { name = "Plain default", defaultCwd = inheritedPlainDir });
        var areaId = (await ReadAsync(areaResp)).GetProperty("id").GetString()!;

        using var inheritedResp = await PostAsync(host, "/api/v1/tracks", "plain-inherited", new { areaId });
        var inheritedId = (await ReadAsync(inheritedResp)).GetProperty("track").GetProperty("id").GetString()!;
        await kernel.WaitForTrackIdleAsync(inheritedId).WaitAsync(Deadline);
        var inheritedTrack = (await kernel.GetTrackAggregateAsync(inheritedId)).Track;
        Assert.Equal((DirectWorkspaceDriver.Id, inheritedPlainDir), (inheritedTrack.WorkspaceDriver, inheritedTrack.Cwd));

        using var explicitCwdResp = await PostAsync(host, "/api/v1/tracks", "plain-explicit-cwd",
            new { areaId, cwd = explicitPlainDir });
        var explicitCwdId = (await ReadAsync(explicitCwdResp)).GetProperty("track").GetProperty("id").GetString()!;
        await kernel.WaitForTrackIdleAsync(explicitCwdId).WaitAsync(Deadline);
        var explicitCwdTrack = (await kernel.GetTrackAggregateAsync(explicitCwdId)).Track;
        Assert.Equal((DirectWorkspaceDriver.Id, explicitPlainDir), (explicitCwdTrack.WorkspaceDriver, explicitCwdTrack.Cwd));

        using var explicitDriverResp = await PostAsync(host, "/api/v1/tracks", "plain-explicit-driver",
            new { areaId, cwd = explicitPlainDir, workspaceDriver = VcsWorkspaceDriver.Id });
        var explicitDriverId = (await ReadAsync(explicitDriverResp)).GetProperty("track").GetProperty("id").GetString()!;
        await kernel.WaitForTrackIdleAsync(explicitDriverId).WaitAsync(Deadline);
        var explicitDriverTrack = (await kernel.GetTrackAggregateAsync(explicitDriverId)).Track;
        Assert.Equal(VcsWorkspaceDriver.Id, explicitDriverTrack.WorkspaceDriver);
        Assert.NotEqual(explicitPlainDir, explicitDriverTrack.Cwd);
        Assert.Equal("explicit", await File.ReadAllTextAsync(Path.Combine(explicitDriverTrack.Cwd, "explicit.txt")));

        using var templateDriverResp = await PostAsync(host, "/api/v1/tracks", "plain-template-driver",
            new { areaId, cwd = explicitPlainDir, templateId = "explicit-vcs" });
        var templateDriverId = (await ReadAsync(templateDriverResp)).GetProperty("track").GetProperty("id").GetString()!;
        await kernel.WaitForTrackIdleAsync(templateDriverId).WaitAsync(Deadline);
        var templateDriverTrack = (await kernel.GetTrackAggregateAsync(templateDriverId)).Track;
        Assert.Equal(VcsWorkspaceDriver.Id, templateDriverTrack.WorkspaceDriver);
        Assert.NotEqual(explicitPlainDir, templateDriverTrack.Cwd);
        Assert.Equal("explicit", await File.ReadAllTextAsync(Path.Combine(templateDriverTrack.Cwd, "explicit.txt")));

        using var gitResp = await PostAsync(host, "/api/v1/tracks", "git-default-vcs",
            new { areaId, cwd = gitDir });
        var gitId = (await ReadAsync(gitResp)).GetProperty("track").GetProperty("id").GetString()!;
        await kernel.WaitForTrackIdleAsync(gitId).WaitAsync(Deadline);
        var gitTrack = (await kernel.GetTrackAggregateAsync(gitId)).Track;
        Assert.Equal(VcsWorkspaceDriver.Id, gitTrack.WorkspaceDriver);
        Assert.NotEqual(gitDir, gitTrack.Cwd);
        Assert.Equal("GitWorktree", gitTrack.WorkspaceSpec.GetProperty("metadata").GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task PlainDirectoryFailsClosedWhenDirectDriverIsUnregistered()
    {
        await using var host = new PergaHostTestCluster(
            configureHostOptions: options => options.DefaultWorkspaceDriver = VcsWorkspaceDriver.Id,
            configureExtraServices: services => services.AddSingleton<IWorkspaceDriverRegistry>(sp =>
                new WorkspaceDriverRegistry([new VcsWorkspaceDriver(sp.GetRequiredService<IVcsService>())])));
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate(
            """
            export default defineTemplate({
              id: "copy-only-template",
              title: "Copy only template",
              workspace: { driver: "vcs-workspace" }
            });
            """);

        var plainDir = Directory.CreateDirectory(Path.Combine(host.DataDir, "plain-no-direct")).FullName;
        var areaId = await CreateAreaAsync(host);

        using var optsResp = await host.AuthenticatedClient.GetAsync(
            $"/api/v1/areas/{areaId}/workspace-options?cwd={Uri.EscapeDataString(plainDir)}");
        var opts = await ReadAsync(optsResp);
        Assert.Equal(JsonValueKind.Null, opts.GetProperty("defaultId").ValueKind);
        Assert.Equal(new[] { "copy" },
            opts.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("id").GetString()));

        using var templateOptsResp = await host.AuthenticatedClient.GetAsync(
            $"/api/v1/areas/{areaId}/workspace-options?cwd={Uri.EscapeDataString(plainDir)}&templateId=copy-only-template");
        var templateOpts = await ReadAsync(templateOptsResp);
        Assert.Equal("copy", templateOpts.GetProperty("defaultId").GetString());
        Assert.Equal(new[] { "copy" },
            templateOpts.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("id").GetString()));

        using var createResp = await PostAsync(host, "/api/v1/tracks", "plain-no-direct-create",
            new { areaId, cwd = plainDir });
        Assert.Equal(HttpStatusCode.Conflict, createResp.StatusCode);
        using var failure = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        Assert.Equal("precondition_failed", failure.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task JjOnlyRepoWithoutJjExecutableFailsClosedInsteadOfCopying()
    {
        var process = new WorkspaceProcess();
        var vcs = new VcsService(process, new VcsServiceOptions { DisableJj = true });
        await using var host = new PergaHostTestCluster(
            configureHostOptions: options => options.DefaultWorkspaceDriver = VcsWorkspaceDriver.Id,
            configureExtraServices: services => services.AddSingleton<IVcsService>(vcs));

        var jjOnlyDir = Directory.CreateDirectory(Path.Combine(host.DataDir, "jj-only-repo")).FullName;
        Directory.CreateDirectory(Path.Combine(jjOnlyDir, ".jj", "repo"));
        await File.WriteAllTextAsync(Path.Combine(jjOnlyDir, "README.md"), "jj only");
        var areaId = await CreateAreaAsync(host);

        using var optsResp = await host.AuthenticatedClient.GetAsync(
            $"/api/v1/areas/{areaId}/workspace-options?cwd={Uri.EscapeDataString(jjOnlyDir)}");
        var opts = await ReadAsync(optsResp);
        Assert.Equal(JsonValueKind.Null, opts.GetProperty("defaultId").ValueKind);
        Assert.Equal(new[] { "direct" },
            opts.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("id").GetString()));

        using var createResp = await PostAsync(host, "/api/v1/tracks", "jj-only-missing-cli",
            new { areaId, cwd = jjOnlyDir });
        Assert.Equal(HttpStatusCode.Conflict, createResp.StatusCode);
        using var failure = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        Assert.Equal("precondition_failed", failure.RootElement.GetProperty("code").GetString());

        using var tracks = await host.AuthenticatedClient.GetAsync($"/api/v1/areas/{areaId}/tracks");
        Assert.Empty((await ReadAsync(tracks)).EnumerateArray());
        Assert.Empty(process.Calls);
    }

    private sealed class WorkspaceProcess : IProcessRunner
    {
        public System.Collections.Concurrent.ConcurrentQueue<ProcessRunRequest> Calls { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
        {
            Calls.Enqueue(request);
            if (request.Arguments.First() == "show-ref")
                return Task.FromResult(new ProcessRunResult(1, "", ""));
            Assert.Equal("worktree", request.Arguments.First());
            Directory.CreateDirectory(request.Arguments.Last());
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        }
    }

    [Fact]
    public async Task CommittedTrackCanBeReplayedReadAndEditedWhileFirstMessageWaitsForWorkspace()
    {
        var runner = new GatedSetup();
        await using var host = CreateHost(runner);
        var areaId = await CreateAreaAsync(host);
        var request = new { areaId, templateId = "gated-setup" };

        var results = await Task.WhenAll(
            PostAsync(host, "/api/v1/tracks", "track-draft", request),
            PostAsync(host, "/api/v1/tracks", "track-draft", request)).WaitAsync(Deadline);
        using var lostResponse = results[0];
        using var replay = results[1];
        var created = await ReadAsync(replay);
        var track = created.GetProperty("track");
        var trackId = track.GetProperty("id").GetString();
        var leadId = track.GetProperty("leadSessionId").GetString();
        Assert.Equal(trackId, (await ReadAsync(lostResponse)).GetProperty("track").GetProperty("id").GetString());

        await runner.Entered.Task.WaitAsync(Deadline);
        try
        {
            using var read = await host.AuthenticatedClient.GetAsync($"/api/v1/tracks/{trackId}").WaitAsync(Deadline);
            Assert.Equal("pending", (await ReadAsync(read)).GetProperty("workspacePreparation").GetString());
            using var patch = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{trackId}", new { title = "Renamed while pending" }).WaitAsync(Deadline);
            Assert.Equal("Renamed while pending", (await ReadAsync(patch)).GetProperty("title").GetString());
            using var before = await host.AuthenticatedClient.GetAsync($"/api/v1/sessions/{leadId}/items").WaitAsync(Deadline);
            var beforePage = await ReadAsync(before);
            Assert.Empty(beforePage.GetProperty("items").EnumerateArray());
            Assert.Empty(beforePage.GetProperty("queuedMessages").EnumerateArray());
            using var followUp = await PostAsync(host, $"/api/v1/sessions/{leadId}/messages", "while-preparing",
                new { content = "Follow-up during setup", mode = "default" });
            Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
            Assert.Empty(host.AgentRuntime.ExecutedTurns);

            using var conflict = await PostAsync(host, "/api/v1/tracks", "track-draft",
                new { areaId, request.templateId, title = "Different title" });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var error = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
            Assert.Equal("idempotency_key_reused", error.RootElement.GetProperty("code").GetString());
            using var tracks = await host.AuthenticatedClient.GetAsync($"/api/v1/areas/{areaId}/tracks");
            Assert.Single((await ReadAsync(tracks)).EnumerateArray());
        }
        finally
        {
            runner.Release.TrySetResult(0);
        }

        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId!).WaitAsync(Deadline);
        Assert.Equal(1, runner.Invocations);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
        using var ready = await host.AuthenticatedClient.GetAsync($"/api/v1/tracks/{trackId}");
        Assert.Equal("Renamed while pending", (await ReadAsync(ready)).GetProperty("track").GetProperty("title").GetString());

        using var deletion = await host.AuthenticatedClient.DeleteAsync($"/api/v1/tracks/{trackId}");
        deletion.EnsureSuccessStatusCode();
        using var deletedReplay = await PostAsync(host, "/api/v1/tracks", "track-draft", request);
        Assert.Equal(HttpStatusCode.Gone, deletedReplay.StatusCode);
    }

    [Fact]
    public async Task DefiniteFailureCanRetryWithinTrackAndKeepsAcceptedMessage()
    {
        var runner = new GatedSetup(failFirst: true);
        await using var host = CreateHost(runner);
        var areaId = await CreateAreaAsync(host);
        using var create = await PostAsync(host, "/api/v1/tracks", "track-draft",
            new { areaId, templateId = "gated-setup" });
        var createdDoc = await ReadAsync(create);
        var trackId = createdDoc.GetProperty("track").GetProperty("id").GetString()!;
        var leadId = createdDoc.GetProperty("track").GetProperty("leadSessionId").GetString()!;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        using var failed = await host.AuthenticatedClient.GetAsync($"/api/v1/tracks/{trackId}");
        Assert.Equal("failed", (await ReadAsync(failed)).GetProperty("workspacePreparation").GetString());
        Assert.Empty(host.AgentRuntime.ExecutedTurns);
        using var overlays = await host.AuthenticatedClient.GetAsync("/api/v1/overlays");
        overlays.EnsureSuccessStatusCode();
        var preview = Assert.Single((await ReadAsync(overlays)).GetProperty("tracks").EnumerateArray());
        Assert.Equal("error", preview.GetProperty("previewKind").GetString());
        Assert.Equal("Workspace initialization exited with code 1.", preview.GetProperty("previewText").GetString());

        using var retry = await host.AuthenticatedClient.PostAsync($"/api/v1/tracks/{trackId}/workspace/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        runner.Release.TrySetResult(0);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Equal(2, runner.Invocations);
        using var msg = await PostAsync(host, $"/api/v1/sessions/{leadId}/messages", "after-retry",
            new { content = "Run after retry" });
        Assert.Equal(HttpStatusCode.OK, msg.StatusCode);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
    }

    [Theory]
    [InlineData(false, "failed", "Workspace initialization exited with code 1.")]
    [InlineData(true, "unknown", "Workspace initialization failed.")]
    public async Task WorkspaceFailurePublishesTrackUpdateAfterWritingTheTerminalState(bool thrown, string phase, string detail)
    {
        var runner = new GatedSetup();
        await using var host = CreateHost(runner);
        var areaId = await CreateAreaAsync(host);
        using var created = await PostAsync(host, "/api/v1/tracks", "track-draft",
            new { areaId, templateId = "gated-setup" });
        var trackId = (await ReadAsync(created)).GetProperty("track").GetProperty("id").GetString();
        await runner.Entered.Task.WaitAsync(Deadline);

        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        using var events = kernel.EventBus.Subscribe();
        var pending = Assert.Single((await kernel.GetOverlaysAsync()).Tracks);
        Assert.True(pending.IsWorking);
        if (thrown)
            runner.Release.SetException(new InvalidOperationException("Details internal to the runner"));
        else
            runner.Release.SetResult(1);

        using var timeout = new CancellationTokenSource(Deadline);
        Perga.Contracts.EventEnvelopeDto update;
        do { update = await events.Reader.ReadAsync(timeout.Token); }
        while (update.Kind != KernelEventKinds.TrackUpdated || update.TrackId != trackId);
        Assert.NotNull(update.EventId);

        using var failed = await host.AuthenticatedClient.GetAsync($"/api/v1/tracks/{trackId}");
        Assert.Equal(phase, (await ReadAsync(failed)).GetProperty("workspacePreparation").GetString());
        var settled = Assert.Single((await kernel.GetOverlaysAsync()).Tracks);
        Assert.False(settled.IsWorking);
        Assert.True(settled.WorkspaceFailed);
        Assert.Equal(Perga.Contracts.TrackOverlayPreviewKind.Error, settled.PreviewKind);
        Assert.Equal(detail, settled.PreviewText);
    }

    [Fact]
    public async Task ClosingDuringPreparationDefersExecutionUntilTrackIsReopened()
    {
        var runner = new GatedSetup();
        await using var host = CreateHost(runner);
        var areaId = await CreateAreaAsync(host);
        using var create = await PostAsync(host, "/api/v1/tracks", "close-reopen",
            new { areaId, templateId = "gated-setup" });
        var createdDoc = await ReadAsync(create);
        var trackId = createdDoc.GetProperty("track").GetProperty("id").GetString()!;
        var leadId = createdDoc.GetProperty("track").GetProperty("leadSessionId").GetString()!;
        var url = $"/api/v1/tracks/{trackId}";
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await runner.Entered.Task.WaitAsync(Deadline);
        using var queued = await PostAsync(host, $"/api/v1/sessions/{leadId}/messages", "defer-while-closed",
            new { content = "Wait until this Track is open" });
        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        using var closed = await host.AuthenticatedClient.PatchAsJsonAsync(url, new { closed = true });
        closed.EnsureSuccessStatusCode();
        runner.Release.TrySetResult(0);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Empty(host.AgentRuntime.ExecutedTurns);

        using var reopened = await host.AuthenticatedClient.PatchAsJsonAsync(url, new { closed = false });
        reopened.EnsureSuccessStatusCode();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
        Assert.Equal(1, runner.Invocations);
    }

    [Fact]
    public async Task AFailedFinalWriteDoesNotRunTheTemplateAgainOnTrackReadOrRetry()
    {
        var runner = new GatedSetup();
        await using var host = CreateHost(runner);
        var areaId = await CreateAreaAsync(host);
        var request = new { areaId, templateId = "gated-setup" };
        using var create = await PostAsync(host, "/api/v1/tracks", "write-failure", request);
        var trackId = (await ReadAsync(create)).GetProperty("track").GetProperty("id").GetString()!;
        var url = $"/api/v1/tracks/{trackId}";
        await runner.Entered.Task.WaitAsync(Deadline);
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync();
        await db.ExecuteAsync(
            """
            CREATE TRIGGER fail_setup_outcome BEFORE UPDATE OF intent_json ON track_workspace_preparations
            BEGIN SELECT RAISE(ABORT, 'injected outcome-write failure'); END;
            """);
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var outcome = kernel.WaitForTrackIdleAsync(trackId);
        runner.Release.TrySetResult(0);
        await Assert.ThrowsAsync<SqliteException>(() => outcome.WaitAsync(Deadline));
        await db.ExecuteAsync("DROP TRIGGER fail_setup_outcome;");

        using var replay = await PostAsync(host, "/api/v1/tracks", "write-failure", request);
        Assert.Equal("unknown", (await ReadAsync(replay)).GetProperty("workspacePreparation").GetString());
        using var retry = await host.AuthenticatedClient.PostAsync($"{url}/workspace/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal(1, runner.Invocations);
        Assert.Empty(host.AgentRuntime.ExecutedTurns);
    }

    [Fact]
    public async Task RestartDuringTemplateCommandRetainsTrackAndNeverRepeatsCommand()
    {
        var directory = Directory.CreateTempSubdirectory("perga-track-creation-");
        try
        {
            string trackId;
            string areaId;
            var firstRunner = new GatedSetup();
            await using (var first = CreateHost(firstRunner, directory.FullName))
            {
                areaId = await CreateAreaAsync(first);
                using var created = await PostAsync(first, "/api/v1/tracks", "restart-track",
                    new { areaId, templateId = "gated-setup" });
                trackId = (await ReadAsync(created)).GetProperty("track").GetProperty("id").GetString()!;
                await firstRunner.Entered.Task.WaitAsync(Deadline);
            }

            var restartedRunner = new GatedSetup();
            await using var restarted = CreateHost(restartedRunner, directory.FullName);
            Assert.Equal(areaId, await CreateAreaAsync(restarted));
            using var replay = await PostAsync(restarted, "/api/v1/tracks", "restart-track",
                new { areaId, templateId = "gated-setup" });
            var snapshot = await ReadAsync(replay);
            Assert.Equal(trackId, snapshot.GetProperty("track").GetProperty("id").GetString());
            Assert.Equal("unknown", snapshot.GetProperty("workspacePreparation").GetString());
            using var retry = await restarted.AuthenticatedClient.PostAsync($"/api/v1/tracks/{trackId}/workspace/retry", null);
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            Assert.Equal(0, restartedRunner.Invocations);
            Assert.Empty(restarted.AgentRuntime.ExecutedTurns);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static PergaHostTestCluster CreateHost(GatedSetup runner, string? dataDir = null)
    {
        var host = new PergaHostTestCluster(dataDir, configureExtraServices: services => services.AddSingleton<IShellCommandRunner>(runner));
        host.Services.GetRequiredService<IPergaKernel>().Templates.RegisterTemplate(
            """
            export default defineTemplate({
              id: "gated-setup",
              title: "Gated setup",
              workspace: { onCreateCmd: "controlled-setup" }
            });
            """);
        return host;
    }

    private static async Task<string> CreateAreaAsync(PergaHostTestCluster host)
    {
        using var response = await PostAsync(host, "/api/v1/areas", "area-draft",
            new { name = "Preparation test", defaultCwd = host.DataDir });
        return (await ReadAsync(response)).GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostAsync(PergaHostTestCluster host, string path, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await host.AuthenticatedClient.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private sealed class GatedSetup(bool failFirst = false) : IShellCommandRunner
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ShellCommandResult> RunAsync(ShellCommandRequest request, CancellationToken ct = default)
        {
            Assert.Equal("controlled-setup", request.Command);
            var count = Interlocked.Increment(ref _invocations);
            Entered.TrySetResult();
            var exitCode = failFirst && count == 1 ? 1 : await Release.Task.WaitAsync(ct);
            return new(exitCode, "", "", false, 0);
        }
    }
}
