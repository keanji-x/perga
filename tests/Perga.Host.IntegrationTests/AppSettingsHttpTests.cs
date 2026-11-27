using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Kernel.Settings;

namespace Perga.Host.IntegrationTests;

public sealed class AppSettingsHttpTests
{
    private const string Endpoint = "/api/v1/settings";
    private const string Provider = "planners.default_provider";
    private const string Compaction = "sessions.auto_compaction_percent";
    private const string Idle = "sessions.idle_timeout_minutes";

    [Fact]
    public async Task LoadsDefaultsWithoutWritingAndPersistsOnlyPatchedValuesAcrossRestart()
    {
        var dir = Directory.CreateTempSubdirectory("perga-settings-http-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.toml");
            var configDir = dir.CreateSubdirectory("config");
            var operatorPath = Path.Combine(configDir.FullName, "perga.toml");
            const string operatorConfig = "[instance]\nname = 'operator'\n";
            await File.WriteAllTextAsync(operatorPath, operatorConfig);
            long savedRevision;
            await using (var host = new PergaHostTestCluster(dir.FullName))
            {
                using var unauthorized = await host.Client.GetAsync(Endpoint);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
                var defaults = await GetAsync(host);
                Assert.Equal("codex", defaults.Values[Provider].GetString());
                Assert.Equal(80, defaults.Values[Compaction].GetInt32());
                Assert.Equal(15, defaults.Values[Idle].GetInt32());
                Assert.False(File.Exists(path));
                Assert.Equal("planners", Assert.Single(defaults.Sections).Id);
                Assert.Equal(new[] { "select", "segmented", "select" }, defaults.Fields.Select(field => field.Control));
                Assert.Contains(defaults.Fields.Single(field => field.Key == Provider).Options!, o => o.Value == "claude");

                using var response = await PatchAsync(host, defaults.Rev, new() { [Idle] = 45, [Compaction] = 85 });
                var saved = await ReadSnapshotAsync(response);
                Assert.Equal(85, saved.Values[Compaction].GetInt32());
                Assert.Equal(45, saved.Values[Idle].GetInt32());
                savedRevision = saved.Rev;
                Assert.True(savedRevision > defaults.Rev);
                Assert.Equal("[sessions]\nauto_compaction_percent = 85\nidle_timeout_minutes = 45\n", await File.ReadAllTextAsync(path));
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }

            await using var restarted = new PergaHostTestCluster(dir.FullName);
            var restored = await GetAsync(restarted);
            Assert.Equal(45, restored.Values[Idle].GetInt32());
            Assert.Equal(85, restored.Values[Compaction].GetInt32());
            using var stale = await PatchAsync(restarted, savedRevision, new() { [Idle] = 30 });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal(operatorConfig, await File.ReadAllTextAsync(operatorPath));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentPatchesOnlyCommitOneRevision()
    {
        await using var host = new PergaHostTestCluster();
        var original = await GetAsync(host);

        var responses = await Task.WhenAll(PatchAsync(host, original.Rev, new() { [Idle] = 30 }),
            PatchAsync(host, original.Rev, new() { [Idle] = 45 }));
        using var first = responses[0];
        using var second = responses[1];
        var success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var conflict = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var error = await ReadErrorAsync(conflict);
        Assert.Equal("conflict", error.GetProperty("code").GetString());
        var committed = await ReadSnapshotAsync(success);
        var current = await GetAsync(host);
        Assert.Equal(committed.Rev, current.Rev);
        Assert.Equal(committed.Values[Idle].GetInt32(), current.Values[Idle].GetInt32());
    }

    [Theory]
    [InlineData("sessions.unknown_setting", 12)]
    [InlineData("Sessions.idle_timeout_minutes", 15)]
    [InlineData(Idle, 4)]
    [InlineData(Idle, 241)]
    [InlineData(Idle, "45")]
    [InlineData(Idle, 30.5)]
    [InlineData(Idle, null)]
    [InlineData(Compaction, 49)]
    [InlineData(Compaction, 96)]
    [InlineData(Compaction, true)]
    [InlineData(Provider, "unregistered-provider")]
    [InlineData(Provider, "CODEX")]
    [InlineData(Provider, "antigravity")]
    public async Task InvalidPatchReturnsAFieldErrorAndLeavesFileUntouched(string field, object? value)
    {
        await using var host = new PergaHostTestCluster();
        var initial = await GetAsync(host);
        using var response = await PatchAsync(host, initial.Rev, new() { [Provider] = "claude", [field] = value });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ReadErrorAsync(response);
        Assert.Equal("invalid_argument", error.GetProperty("code").GetString());
        Assert.Equal(field, error.GetProperty("field").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("error").GetString()));
        Assert.Equal(initial.Rev, (await GetAsync(host)).Rev);
        Assert.False(File.Exists(Path.Combine(host.DataDir, "settings.toml")));
    }

    [Theory]
    [InlineData("""{"patch":{}}""", "body")]
    [InlineData("""{"expectedRev":"1","patch":{}}""", "expectedRev")]
    [InlineData("""{"expectedRev":1,"patch":null}""", "patch")]
    [InlineData("""{"ExpectedRev":1,"patch":{}}""", "ExpectedRev")]
    [InlineData("""{"expectedRev":1,"Patch":{}}""", "Patch")]
    [InlineData("""{"expectedRev":1,"patch":{},"extra":true}""", "extra")]
    public async Task InvalidEnvelopeHasAFieldError(string body, string field)
    {
        await using var host = new PergaHostTestCluster();
        using var response = await host.AuthenticatedClient.PatchAsync(Endpoint,
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(field, (await ReadErrorAsync(response)).GetProperty("field").GetString());
    }

    [Fact]
    public async Task MalformedJsonStringReturnsAnInvalidArgumentForItsSetting()
    {
        await using var host = new PergaHostTestCluster();
        var initial = await GetAsync(host);
        var body = $$"""{"expectedRev":{{initial.Rev}},"patch":{"{{Provider}}":"\uD800"} }""";
        using var response = await host.AuthenticatedClient.PatchAsync(Endpoint,
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ReadErrorAsync(response);
        Assert.Equal("invalid_argument", error.GetProperty("code").GetString());
        Assert.Equal(Provider, error.GetProperty("field").GetString());
        Assert.Equal(initial.Rev, (await GetAsync(host)).Rev);
        Assert.False(File.Exists(Path.Combine(host.DataDir, "settings.toml")));
    }

    [Fact]
    public async Task ReloadsExternalEditsAndRejectsAStaleWriteEvenWithoutAnInterveningGet()
    {
        await using var host = new PergaHostTestCluster();
        var original = await GetAsync(host);
        var path = Path.Combine(host.DataDir, "settings.toml");
        await WriteManualSettingsAsync(path, "[sessions]\nauto_compaction_percent = 85\n");
        var originalTime = File.GetLastWriteTimeUtc(path);
        var edited = await GetAsync(host);
        Assert.NotEqual(original.Rev, edited.Rev);
        Assert.Equal(85, edited.Values[Compaction].GetInt32());

        const string manual = "[sessions]\nauto_compaction_percent = 88\n";
        await WriteManualSettingsAsync(path, manual);
        File.SetLastWriteTimeUtc(path, originalTime);
        using var conflict = await PatchAsync(host, edited.Rev, new() { [Idle] = 45 });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(manual, await File.ReadAllTextAsync(path));
        var current = await GetAsync(host);
        Assert.Equal(88, current.Values[Compaction].GetInt32());
        using var merged = await PatchAsync(host, current.Rev, new() { [Idle] = 45 });
        Assert.Equal(88, (await ReadSnapshotAsync(merged)).Values[Compaction].GetInt32());

        File.Delete(path);
        var removed = await GetAsync(host);
        Assert.Equal(80, removed.Values[Compaction].GetInt32());
        Assert.Equal(15, removed.Values[Idle].GetInt32());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("[sessions\nidle_timeout_minutes = 30", "Line 1")]
    [InlineData("[sessions]\nidle_timeout_minutes = 400", Idle)]
    public async Task CorruptedFileRefusesReadsAndWritesUntilRepaired(string corrupt, string diagnostic)
    {
        await using var host = new PergaHostTestCluster();
        var initial = await GetAsync(host);
        var path = Path.Combine(host.DataDir, "settings.toml");

        await WriteManualSettingsAsync(path, corrupt);
        using var get = await host.AuthenticatedClient.GetAsync(Endpoint);
        using var patch = await PatchAsync(host, initial.Rev, new() { [Idle] = 30 });
        foreach (var response in new[] { get, patch })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var error = await ReadErrorAsync(response);
            Assert.Equal("settings_unavailable", error.GetProperty("code").GetString());
            Assert.Contains(diagnostic, error.GetProperty("error").GetString());
        }
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
        Assert.Equal(initial.Rev, host.Services.GetRequiredService<AppSettingsService>().Current.Contract.Rev);

        await WriteManualSettingsAsync(path, "[sessions]\nidle_timeout_minutes = 30\n");
        var repaired = await GetAsync(host);
        Assert.Equal(30, repaired.Values[Idle].GetInt32());
        Assert.NotEqual(initial.Rev, repaired.Rev);
    }

    [Fact]
    public async Task MalformedFileFailsHostStartup()
    {
        var dir = Directory.CreateTempSubdirectory("perga-settings-boot-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.toml");
            const string malformed = "[broken";
            await WriteManualSettingsAsync(path, malformed);
            await using var factory = new IsolatedPergaHostFactory(dir.FullName);
            var error = Assert.Throws<AppSettingsLoadException>(() => factory.CreateClient());
            Assert.Contains("Line 1", error.Message);
            Assert.Equal(malformed, await File.ReadAllTextAsync(path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TrackPermissionChoiceDoesNotApplyTheHostModelToAnotherProvider()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
        {
            options.DefaultProvider = "codex";
            options.DefaultModel = "gpt-5.4";
        });
        var initial = await GetAsync(host);
        using var patch = await PatchAsync(host, initial.Rev, new() { [Provider] = "claude" });
        await ReadSnapshotAsync(patch);

        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new("Planner settings", DefaultCwd: host.DataDir));
        var permissionOnly = new { areaId = area.Id, provider = "claude", permissionMode = "acceptEdits" };
        var lead = await CreateLeadAsync(permissionOnly);
        Assert.Equal("claude", lead.Provider);
        Assert.Equal("acceptEdits", lead.PermissionMode);
        Assert.Null(lead.Model);

        Assert.Equal("sonnet", (await CreateLeadAsync(new
        {
            permissionOnly.areaId, permissionOnly.provider, permissionOnly.permissionMode, model = "sonnet"
        })).Model);
        Assert.Equal("gpt-5.4", (await CreateLeadAsync(new { areaId = area.Id, provider = "codex" })).Model);

        async Task<AgentSessionDto> CreateLeadAsync(object request)
        {
            using var response = await host.AuthenticatedClient.PostAsJsonAsync("/api/v1/tracks", request,
                PergaJsonDefaults.ApiOptions);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var snapshot = await response.Content.ReadFromJsonAsync<TrackAggregateSnapshot>(PergaJsonDefaults.ApiOptions);
            return Assert.Single(snapshot!.Sessions);
        }
    }

    [Fact]
    public async Task UpdatedDefaultsApplyToNewTracksAndExistingActorRuntimes()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options => options.DefaultModel = "gpt-5.4");
        var (track, originalLead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var human = new InteropPrincipal.Human();
        var compactCount = 0;
        host.AgentRuntime.CompactHandler = _ =>
        {
            Interlocked.Increment(ref compactCount);
            return Task.FromResult(new NativeCompactResult());
        };
        host.AgentRuntime.SetTurnHandler(UseThreeQuartersContext);

        await RunTurnAsync("before");
        Assert.Equal(0, Volatile.Read(ref compactCount));

        var original = await GetAsync(host);
        using var patch = await PatchAsync(host, original.Rev,
            new() { [Provider] = "claude", [Compaction] = 70, [Idle] = 5 });
        await ReadSnapshotAsync(patch);

        host.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await kernel.Actors.EvictIdleSessionRuntimesAsync());
        Assert.Single(host.AgentRuntime.DisposedSessions);
        await RunTurnAsync("after");
        Assert.Equal(1, Volatile.Read(ref compactCount));
        var retained = await kernel.GetSessionItemsAsync(originalLead, new(), human, default);
        Assert.Equal("codex", retained.Session.Provider);

        var (_, newLeadId) = await host.CreateTrackAsync();
        var newLead = await kernel.GetSessionItemsAsync(newLeadId, new(), human, default);
        Assert.Equal("claude", newLead.Session.Provider);
        Assert.Null(newLead.Session.Model);

        async Task RunTurnAsync(string key)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await kernel.SendSessionMessageAsync(originalLead, new(key, key), human, deadline.Token);
            await kernel.WaitForTrackIdleAsync(track, deadline.Token);
        }

        static async IAsyncEnumerable<UnifiedAgentEvent> UseThreeQuartersContext(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new UnifiedAgentEvent.ThreadBound(spec.ResumeNativeThreadId ?? "settings-test-thread");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(10, 5, 0, 1000, UsedTokens: 750));
        }
    }

    private static async Task WriteManualSettingsAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task<SettingsSnapshotDto> GetAsync(PergaHostTestCluster host)
    {
        using var response = await host.AuthenticatedClient.GetAsync(Endpoint);
        return await ReadSnapshotAsync(response);
    }

    private static async Task<SettingsSnapshotDto> ReadSnapshotAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SettingsSnapshotDto>(PergaJsonDefaults.ApiOptions))!;
    }

    private static Task<HttpResponseMessage> PatchAsync(PergaHostTestCluster host, long rev, Dictionary<string, object?> patch) =>
        host.AuthenticatedClient.PatchAsJsonAsync(Endpoint, new { expectedRev = rev, patch });

    private static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
