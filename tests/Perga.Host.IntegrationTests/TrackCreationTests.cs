using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests;

public sealed class TrackCreationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WorkspaceOptionsAreReadOnlyAndAnExplicitGitChoiceIsPersistedAndReplayed()
    {
        var process = new WorkspaceProcess();
        var vcs = new VcsService(process, new VcsServiceOptions { AutoColocateJj = true, JjExecutablePath = "jj" });
        await using var host = new PergaHostTestCluster(
            configureExtraServices: services => services.AddSingleton<IVcsService>(vcs));
        var areaId = await CreateAreaAsync(host);
        using var inherited = await host.Client.GetAsync($"/api/v1/areas/{areaId}/workspace-options");
        Assert.Equal("copy", (await ReadAsync(inherited)).GetProperty("defaultId").GetString());
        using var cleared = await host.Client.GetAsync($"/api/v1/areas/{areaId}/workspace-options?cwd=");
        Assert.Equal("managed", (await ReadAsync(cleared)).GetProperty("defaultId").GetString());
        var source = Path.Combine(host.DataDir, "colocated");
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        await File.WriteAllTextAsync(Path.Combine(source, ".git", "HEAD"), "ref: refs/heads/main\n");
        var optionsPath = $"/api/v1/areas/{areaId}/workspace-options?cwd={Uri.EscapeDataString(source)}";
        using var gitOnly = await host.Client.GetAsync(optionsPath);
        Assert.Equal("git", (await ReadAsync(gitOnly)).GetProperty("defaultId").GetString());
        Assert.False(Directory.Exists(Path.Combine(source, ".jj")));
        Assert.Empty(process.Calls);

        Directory.CreateDirectory(Path.Combine(source, ".jj", "repo"));
        using var colocated = await host.Client.GetAsync(optionsPath);
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
        using var tracks = await host.Client.GetAsync($"/api/v1/areas/{areaId}/tracks");
        Assert.Empty((await ReadAsync(tracks)).EnumerateArray());

        var body = new { areaId, cwd = source, workspaceOptionId = "git" };
        using var created = await PostAsync(host, "/api/v1/tracks", "chosen-git", body);
        var id = (await ReadAsync(created)).GetProperty("track").GetProperty("id").GetString()!;
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(id).WaitAsync(Deadline);
        using var ready = await host.Client.GetAsync($"/api/v1/tracks/{id}");
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
        using var managedReady = await host.Client.GetAsync($"/api/v1/tracks/{managedId}");
        var managedSpec = (await ReadAsync(managedReady)).GetProperty("track").GetProperty("workspaceSpec");
        Assert.True(managedSpec.GetProperty("managedEmptyWorkspace").GetBoolean());
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
        var request = new { areaId, templateId = "gated-setup", firstMessage = "Keep this message" };

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
            using var read = await host.Client.GetAsync($"/api/v1/tracks/{trackId}").WaitAsync(Deadline);
            Assert.Equal("pending", (await ReadAsync(read)).GetProperty("workspacePreparation").GetString());
            using var patch = await host.Client.PatchAsJsonAsync($"/api/v1/tracks/{trackId}", new { title = "Renamed while pending" }).WaitAsync(Deadline);
            Assert.Equal("Renamed while pending", (await ReadAsync(patch)).GetProperty("title").GetString());
            using var before = await host.Client.GetAsync($"/api/v1/sessions/{leadId}/items").WaitAsync(Deadline);
            var beforePage = await ReadAsync(before);
            var item = Assert.Single(beforePage.GetProperty("items").EnumerateArray());
            Assert.Empty(beforePage.GetProperty("queuedMessages").EnumerateArray());
            using var followUp = await PostAsync(host, $"/api/v1/sessions/{leadId}/messages", "while-preparing",
                new { content = "Follow-up during setup", queueIfBusy = true });
            Assert.Equal(HttpStatusCode.Conflict, followUp.StatusCode);
            using var setupError = JsonDocument.Parse(await followUp.Content.ReadAsStringAsync());
            Assert.Equal("precondition_failed", setupError.RootElement.GetProperty("code").GetString());
            Assert.Equal(request.firstMessage, item.GetProperty("payload").GetProperty("content").GetString());
            Assert.Equal("user", item.GetProperty("kind").GetString());
            Assert.Empty(host.AgentRuntime.ExecutedTurns);

            using var conflict = await PostAsync(host, "/api/v1/tracks", "track-draft",
                new { areaId, request.templateId, firstMessage = "Different text" });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var error = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
            Assert.Equal("idempotency_key_reused", error.RootElement.GetProperty("code").GetString());
            using var tracks = await host.Client.GetAsync($"/api/v1/areas/{areaId}/tracks");
            Assert.Single((await ReadAsync(tracks)).EnumerateArray());
        }
        finally
        {
            runner.Release.TrySetResult(0);
        }

        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId!).WaitAsync(Deadline);
        Assert.Equal(1, runner.Invocations);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
        using var ready = await host.Client.GetAsync($"/api/v1/tracks/{trackId}");
        Assert.Equal("Renamed while pending", (await ReadAsync(ready)).GetProperty("track").GetProperty("title").GetString());

        using var deletion = await host.Client.DeleteAsync($"/api/v1/tracks/{trackId}");
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
            new { areaId, templateId = "gated-setup", firstMessage = "Run after retry" });
        var trackId = (await ReadAsync(create)).GetProperty("track").GetProperty("id").GetString()!;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        using var failed = await host.Client.GetAsync($"/api/v1/tracks/{trackId}");
        Assert.Equal("failed", (await ReadAsync(failed)).GetProperty("workspacePreparation").GetString());
        Assert.Empty(host.AgentRuntime.ExecutedTurns);

        using var retry = await host.Client.PostAsync($"/api/v1/tracks/{trackId}/workspace/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        runner.Release.TrySetResult(0);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Equal(2, runner.Invocations);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
    }

    [Fact]
    public async Task ClosingDuringPreparationDefersExecutionUntilTrackIsReopened()
    {
        var runner = new GatedSetup();
        await using var host = CreateHost(runner);
        var areaId = await CreateAreaAsync(host);
        using var create = await PostAsync(host, "/api/v1/tracks", "close-reopen",
            new { areaId, templateId = "gated-setup", firstMessage = "Wait until this Track is open" });
        var trackId = (await ReadAsync(create)).GetProperty("track").GetProperty("id").GetString()!;
        var url = $"/api/v1/tracks/{trackId}";
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await runner.Entered.Task.WaitAsync(Deadline);
        using var closed = await host.Client.PatchAsJsonAsync(url, new { closed = true });
        closed.EnsureSuccessStatusCode();
        runner.Release.TrySetResult(0);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Empty(host.AgentRuntime.ExecutedTurns);

        using var reopened = await host.Client.PatchAsJsonAsync(url, new { closed = false });
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
        var request = new { areaId, templateId = "gated-setup", firstMessage = "Keep the queued message" };
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
        using var retry = await host.Client.PostAsync($"{url}/workspace/retry", null);
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
                    new { areaId, templateId = "gated-setup", firstMessage = "Still waiting" });
                trackId = (await ReadAsync(created)).GetProperty("track").GetProperty("id").GetString()!;
                await firstRunner.Entered.Task.WaitAsync(Deadline);
            }

            var restartedRunner = new GatedSetup();
            await using var restarted = CreateHost(restartedRunner, directory.FullName);
            Assert.Equal(areaId, await CreateAreaAsync(restarted));
            using var replay = await PostAsync(restarted, "/api/v1/tracks", "restart-track",
                new { areaId, templateId = "gated-setup", firstMessage = "Still waiting" });
            var snapshot = await ReadAsync(replay);
            Assert.Equal(trackId, snapshot.GetProperty("track").GetProperty("id").GetString());
            Assert.Equal("unknown", snapshot.GetProperty("workspacePreparation").GetString());
            using var retry = await restarted.Client.PostAsync($"/api/v1/tracks/{trackId}/workspace/retry", null);
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
        return await host.Client.SendAsync(request);
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
