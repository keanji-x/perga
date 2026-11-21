using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Templates;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class EventHubAndTemplateTests
{
    [Fact]
    public async Task EventHub_BroadcastsPersistentWithNonNullId_AndTransientWithNullIdNeverStored()
    {
        await using var ctx = new KernelTestContext();
        using var allSub = ctx.EventHub.Subscribe();
        using var filteredSub = ctx.EventHub.Subscribe("trk_other");

        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var leadId = snap.Track.LeadSessionId!;

        // Send a message to trigger transient text/thinking deltas AND persistent turn.completed + session.updated
        await ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("Hello Perga", "idem-1"));

        var receivedAll = new List<EventEnvelopeDto>();
        while (allSub.Reader.TryRead(out var evt))
        {
            receivedAll.Add(evt);
        }

        var receivedFiltered = new List<EventEnvelopeDto>();
        while (filteredSub.Reader.TryRead(out var evt))
        {
            receivedFiltered.Add(evt);
        }

        Assert.Empty(receivedFiltered);

        // Verify transient events have EventId == null and are NEVER in Store.PersistedEvents
        var transients = receivedAll.Where(e => KernelEventKinds.IsTransient(e.Kind)).ToArray();
        Assert.Contains(transients, e => e.Kind == KernelEventKinds.SessionTextDelta);
        Assert.Contains(transients, e => e.Kind == KernelEventKinds.SessionThinkingDelta);
        Assert.All(transients, e => Assert.Null(e.EventId));
        Assert.DoesNotContain(ctx.Store.PersistedEvents, e => KernelEventKinds.IsTransient(e.Kind));

        // Verify persistent events have EventId != null
        var persistents = receivedAll.Where(e => KernelEventKinds.IsPersistent(e.Kind)).ToArray();
        Assert.Contains(persistents, e => e.Kind == KernelEventKinds.TrackUpdated);
        Assert.Contains(persistents, e => e.Kind == KernelEventKinds.TurnCompleted);
        Assert.Contains(persistents, e => e.Kind == KernelEventKinds.SessionUpdated);
        Assert.All(persistents, e => Assert.NotNull(e.EventId));

        // Rejects misclassified publish attempts
        Assert.Throws<ArgumentException>(() =>
            ctx.EventHub.PublishTransient("area_1", "trk_1", KernelEventKinds.TrackUpdated, TestJson.Parse("{}"), 1000));
        Assert.Throws<ArgumentException>(() =>
            ctx.EventHub.PublishPersistent(new EventEnvelopeDto(1, "area_1", "trk_1", KernelEventKinds.SessionTextDelta, TestJson.Parse("{}"), 1000)));
    }

    [Fact]
    public void TemplateCatalog_LoadsFromDirectory_AndCachesPerTrackSnapshotsByHash()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-tpl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "alpha.perga.js"),
                """export default defineTemplate({ id: "alpha", title: "Alpha Template" });""");
            File.WriteAllText(
                Path.Combine(tempDir, "beta.perga.js"),
                """export default defineTemplate({ id: "beta", title: "Beta Template" });""");

            var vm = new ConfigurableFakePolicyVm();
            var catalog = new TemplateCatalog(vm, tempDir);

            var manifests = catalog.ListManifests();
            Assert.Equal(2, manifests.Count);
            Assert.Equal("alpha", manifests[0].Id);
            Assert.Equal("beta", manifests[1].Id);
            Assert.Equal(2, vm.CompileCallCount);

            // Compiling an identical per-track snapshot source twice hits SHA-256 cache without re-invoking IPolicyVm.Compile
            const string customSnapshot = """export default defineTemplate({ id: "pinned-v1", title: "Pinned Snapshot" });""";
            var p1 = catalog.GetOrCompileSnapshot(customSnapshot);
            var p2 = catalog.GetOrCompileSnapshot(customSnapshot);
            Assert.Same(p1, p2);
            Assert.Equal(3, vm.CompileCallCount);
            Assert.NotSame(p2, catalog.CompileSource(customSnapshot));
            Assert.Same(p2, catalog.GetOrCompileSnapshot(customSnapshot));
            Assert.Equal(4, vm.CompileCallCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
