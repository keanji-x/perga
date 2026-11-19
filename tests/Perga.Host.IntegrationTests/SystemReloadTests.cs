using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Host.SystemUpgrade;
using Perga.Kernel;

namespace Perga.Host.IntegrationTests;

public sealed class SystemReloadTests
{
    [Fact]
    public async Task ReloadActivatesWebAndTemplatesWhileRetainingOldAssetsAndResidentIdentity()
    {
        using var installation = new Installation();
        installation.AddRelease("old", "web-old", "old");
        installation.AddRelease("new", "web-new", "new");
        installation.Activate("old");
        await using var host = new PergaHostTestCluster(Path.Combine(installation.Root, "data"),
            configureHostOptions: options => options.InstallationRoot = installation.Root);
        var before = await Info(host);
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        Assert.Equal("old", kernel.Templates.GetRequiredPolicy("reload-test").Manifest.Title);
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Reload snapshots", DefaultCwd: host.DataDir));
        var oldTrack = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Cwd: host.DataDir, TemplateId: "reload-test"));
        var storedBefore = (await host.Services.GetRequiredService<Perga.Kernel.Ports.ITrackStore>().LoadTrackAggregateAsync(oldTrack.Track.Id, default))!.TemplateSnapshotSource;
        Assert.NotNull(storedBefore);
        using var oldHtml = await host.Client.GetAsync("/");
        Assert.Equal("no-cache", oldHtml.Headers.CacheControl!.ToString());
        Assert.Contains("old", await oldHtml.Content.ReadAsStringAsync());
        installation.Activate("new");
        using var response = await host.Client.PostAsJsonAsync("/api/v1/system/reload", new { releaseId = "new", webBuildId = "web-new" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await Info(host);
        Assert.Equal("new", after.ReleaseId);
        Assert.Equal("web-new", after.WebBuildId);
        Assert.Equal(before.HostBuildId, after.HostBuildId);
        Assert.Equal(before.BridgeBuildId, after.BridgeBuildId);
        Assert.Equal(before.SchemaVersion, after.SchemaVersion);
        Assert.Equal("new", kernel.Templates.GetRequiredPolicy("reload-test").Manifest.Title);
        var store = host.Services.GetRequiredService<Perga.Kernel.Ports.ITrackStore>();
        Assert.Equal(storedBefore, (await store.LoadTrackAggregateAsync(oldTrack.Track.Id, default))!.TemplateSnapshotSource);
        var newTrack = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Cwd: host.DataDir, TemplateId: "reload-test"));
        Assert.Contains("new", (await store.LoadTrackAggregateAsync(newTrack.Track.Id, default))!.TemplateSnapshotSource);
        Assert.Equal("old asset", await host.Client.GetStringAsync("/assets/old-abc123.js"));
        Assert.Equal("new asset", await host.Client.GetStringAsync("/assets/new-abc123.js"));
        using var next = await host.Client.GetAsync("/next");
        Assert.Equal("no-cache", next.Headers.CacheControl!.ToString());
        Assert.Contains("new", await next.Content.ReadAsStringAsync());
        using var fallback = await host.Client.GetAsync("/tracks/existing");
        Assert.Equal("no-cache", fallback.Headers.CacheControl!.ToString());
        Assert.Contains("new", await fallback.Content.ReadAsStringAsync());
        Assert.Empty(host.AgentRuntime.OpenedSessions);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("missing-web")]
    [InlineData("missing-templates")]
    [InlineData("identity")]
    [InlineData("template")]
    [InlineData("link")]
    public async Task FailedReloadKeepsPreviousSnapshot(string failure)
    {
        using var installation = new Installation();
        installation.AddRelease("old", "web-old", "old");
        installation.AddRelease("new", "web-new", "new");
        installation.Activate("old");
        await using var host = new PergaHostTestCluster(Path.Combine(installation.Root, "data"),
            configureHostOptions: options => options.InstallationRoot = installation.Root);
        var directory = Path.Combine(installation.Root, "releases", "new");
        switch (failure)
        {
            case "manifest":
                await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), "{\"formatVersion\":1}");
                break;
            case "missing-web":
                Directory.Delete(Path.Combine(directory, "web"), true);
                break;
            case "missing-templates":
                Directory.Delete(Path.Combine(directory, "templates"), true);
                break;
            case "template":
                await File.WriteAllTextAsync(Path.Combine(directory, "templates", "reload.perga.js"), "invalid javascript (");
                installation.Manifest("new", "web-new");
                break;
            case "link":
                Directory.Delete(Path.Combine(directory, "web"), true);
                Directory.CreateSymbolicLink(Path.Combine(directory, "web"), Path.Combine(installation.Root, "releases", "old", "web"));
                break;
        }
        installation.Activate("new");
        using var response = await host.Client.PostAsJsonAsync("/api/v1/system/reload", new { releaseId = failure == "identity" ? "wrong" : "new", webBuildId = "web-new" });
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal("old", (await Info(host)).ReleaseId);
        Assert.Contains("old", await host.Client.GetStringAsync("/"));
        Assert.Equal("old", host.Services.GetRequiredService<IPergaKernel>().Templates.GetRequiredPolicy("reload-test").Manifest.Title);
    }

    [Fact]
    public async Task ReloadRetainsOnlyThreePreviousAssetProviders()
    {
        using var installation = new Installation();
        for (var i = 0; i < 5; i++)
            installation.AddRelease("r" + i, "web-" + i, "r" + i);
        installation.Activate("r0");
        await using var host = new PergaHostTestCluster(Path.Combine(installation.Root, "data"),
            configureHostOptions: options => options.InstallationRoot = installation.Root);
        var snapshots = host.Services.GetRequiredService<ReleaseSnapshotManager>();
        for (var i = 1; i < 5; i++)
        {
            installation.Activate("r" + i);
            snapshots.Reload("r" + i, "web-" + i);
        }
        Assert.False(snapshots.GetFileInfo("assets/r0-abc123.js").Exists);
        for (var i = 1; i < 5; i++)
            Assert.True(snapshots.GetFileInfo("assets/r" + i + "-abc123.js").Exists);
    }

    [Fact]
    public async Task BootAndReloadDoNotReadResidentBinariesOrRehashWebBytes()
    {
        using var installation = new Installation();
        installation.AddRelease("old", "web-old", "old");
        installation.AddRelease("new", "web-new", "new");
        foreach (var id in new[] { "old", "new" })
        {
            var directory = Path.Combine(installation.Root, "releases", id);
            Directory.Delete(Path.Combine(directory, "host"), true);
            Directory.Delete(Path.Combine(directory, "termBridge"), true);
            File.WriteAllText(Path.Combine(directory, "web", "index.html"), "<html>updated bytes</html>");
        }
        installation.Activate("old");
        await using var host = new PergaHostTestCluster(Path.Combine(installation.Root, "data"),
            configureHostOptions: options => options.InstallationRoot = installation.Root);
        Assert.Equal("old", (await Info(host)).ReleaseId);
        installation.Activate("new");
        using var response = await host.Client.PostAsJsonAsync("/api/v1/system/reload", new { releaseId = "new", webBuildId = "web-new" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("updated bytes", await host.Client.GetStringAsync("/"));
    }

    private static async Task<SystemInfoDto> Info(PergaHostTestCluster host) =>
        (await host.Client.GetFromJsonAsync<SystemInfoDto>("/api/v1/system/info", PergaJsonDefaults.ApiOptions))!;

    private sealed class Installation : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("perga-reload-").FullName;
        public void AddRelease(string id, string webId, string text)
        {
            var directory = Path.Combine(Root, "releases", id);
            foreach (var component in new[] { "host", "web/assets", "templates", "termBridge" }) Directory.CreateDirectory(Path.Combine(directory, component));
            File.WriteAllText(Path.Combine(directory, "host", "Perga.Host"), "resident host");
            File.WriteAllText(Path.Combine(directory, "termBridge", "perga-term-bridge"), "resident bridge");
            if (!OperatingSystem.IsWindows())
                foreach (var path in new[] { "host/Perga.Host", "termBridge/perga-term-bridge" })
                    File.SetUnixFileMode(Path.Combine(directory, path), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(directory, "web", "index.html"), "<html>" + text + "</html>");
            File.WriteAllText(Path.Combine(directory, "web", "assets", text + "-abc123.js"), text + " asset");
            File.WriteAllText(Path.Combine(directory, "templates", "reload.perga.js"),
                "export default defineTemplate({ id: 'reload-test', title: '" + text + "', before() { return Allow(); } });");
            Manifest(id, webId);
        }
        public void Manifest(string id, string webId)
        {
            var directory = Path.Combine(Root, "releases", id);
            var components = new[] { "host", "web", "templates", "termBridge" }.ToDictionary(c => c, c => new string('a', 64));
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { formatVersion = 1, releaseId = id, webBuildId = webId, schemaVersion = 11, components }));
        }
        public void Activate(string id)
        {
            var next = Path.Combine(Root, "next-link");
            Directory.CreateSymbolicLink(next, Path.Combine("releases", id));
            var current = Path.Combine(Root, "current");
            if (Directory.Exists(current)) Directory.Delete(current);
            Directory.Move(next, current);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
