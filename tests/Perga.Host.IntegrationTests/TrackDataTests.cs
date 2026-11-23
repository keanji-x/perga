using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class TrackDataTests
{
    private const string Source = """
        export default defineTemplate({
          id: 'ledger', title: 'Ledger',
          before(action, ctx) {
            if (action.type === 'tool.data_patch') {
              if (ctx.data.records?.[0]?.value !== 1) return Deny('Expected current ledger');
              ctx.data.records[0].value = 98;
              ctx.data.approvals = 1;
            }
            return Allow();
          },
          data: {
            schema: t => ({ records: t.array(t.object({ code: t.id(), value: t.number(), note: t.string().optional() })) }),
            initial: () => ({ records: [{ code: 'a', value: 1, note: 'keep' }, { code: 'b', value: 2 }] }),
          },
          report: { views: { ledger: data => data.records } }
        });
        """;

    [Fact]
    public async Task DataPatchesAndTemplateChangesPreserveDataAuthorityAcrossRestart()
    {
        var directory = Directory.CreateTempSubdirectory("perga-track-data-");
        try
        {
            string id;
            JsonElement expected;
            await using (var host = NewHost(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                kernel.Templates.RegisterTemplate(Source);
                var area = await kernel.CreateAreaAsync(new("Data", DefaultCwd: host.DataDir));
                var created = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "ledger"))).Track;
                id = created.Id;
                var url = $"/api/v1/tracks/{id}";
                var principal = await kernel.OpenMcpCallerAsync(created.LeadSessionId!);
                Task<JsonElement> Apply(string tool, object args) => KernelMcpDispatcher.ExecuteGuardedAsync(
                    kernel, principal, tool, JsonSerializer.SerializeToElement(args));

                Assert.Equal(0, created.DataRev);
                Assert.Equal(2, created.Data.GetProperty("records").GetArrayLength());

                var first = await Apply(KernelMcpToolNames.DataPatch, new
                {
                    expectedDataRev = 0,
                    patch = Parse("""{"records":{"$upsert":[{"code":"a","value":5},{"code":"c","value":3}],"$delete":["b"]}}""")
                });
                Assert.True(first.GetProperty("ok").GetBoolean());
                Assert.Equal(1, first.GetProperty("dataRev").GetInt64());
                var records = first.GetProperty("data").GetProperty("records");
                Assert.Equal("keep", records[0].GetProperty("note").GetString());
                Assert.Equal(5, records[0].GetProperty("value").GetInt32());
                Assert.Equal("c", records[1].GetProperty("code").GetString());
                Assert.False((await kernel.GetTrackAggregateAsync(id)).Track.Data.TryGetProperty("approvals", out _));

                using var invalid = await host.AuthenticatedClient.PatchAsJsonAsync(url + "/data",
                    new { expectedDataRev = 1, patch = Parse("""{"records":{"$upsert":[{"code":"a","value":"invalid"}]}}""") });
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

                using var typoEnvelope = await host.AuthenticatedClient.PatchAsJsonAsync(url + "/data",
                    new { expectedRev = 1, patch = Parse("""{"records":{"$upsert":[{"code":"a","value":99}]}}""") });
                Assert.Equal(HttpStatusCode.BadRequest, typoEnvelope.StatusCode);

                var reportWithoutWidgets = await Apply(KernelMcpToolNames.ReportRead, new { });
                Assert.Equal(1, reportWithoutWidgets.GetProperty("dataRev").GetInt64());

                var patch = Parse("""{"records":{"$upsert":[{"code":"a","value":6}]}}""");
                var competing = await Task.WhenAll(
                    host.AuthenticatedClient.PatchAsJsonAsync(url + "/data", new { expectedDataRev = 1, patch }),
                    host.AuthenticatedClient.PatchAsJsonAsync(url + "/data", new { expectedDataRev = 1, patch }));
                using var winner = Assert.Single(competing, result => result.StatusCode == HttpStatusCode.OK);
                using var stale = Assert.Single(competing, result => result.StatusCode == HttpStatusCode.Conflict);
                var winnerBody = await winner.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(2, winnerBody.GetProperty("dataRev").GetInt64());

                using var retry = await host.AuthenticatedClient.PatchAsJsonAsync(url + "/data", patch);
                retry.EnsureSuccessStatusCode();
                var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(2, retryBody.GetProperty("dataRev").GetInt64());
                expected = retryBody.GetProperty("data").Clone();

                var views = await host.AuthenticatedClient.GetFromJsonAsync<JsonElement>(url + "/views");
                Assert.Equal(2, views.GetProperty("dataRev").GetInt64());
                Assert.Equal(6, views.GetProperty("views").GetProperty("ledger")[0].GetProperty("value").GetInt32());

                var switched = await Apply(KernelMcpToolNames.TrackSet, new { source = Source.Replace("'ledger'", "'compatible'") });
                Assert.Equal(2, switched.GetProperty("dataRev").GetInt64());
                Assert.True(JsonElement.DeepEquals(expected, switched.GetProperty("data")));

                const string incompatible = """
                    export default defineTemplate({ id: 'other', title: 'Other',
                      data: { schema: t => ({ summary: t.string() }), initial: () => ({ summary: 'reset' }) } });
                    """;
                var failure = await Assert.ThrowsAsync<KernelException>(() =>
                    Apply(KernelMcpToolNames.TrackSet, new { source = incompatible }));
                Assert.Equal(KernelErrorCode.Conflict, failure.Code);
                Assert.Contains("summary", failure.Message);
                Assert.Equal("compatible", (await kernel.GetTrackAggregateAsync(id)).Track.TemplateId);

                var reset = await Apply(KernelMcpToolNames.TrackSet, new { source = incompatible, resetData = true });
                Assert.Equal(3, reset.GetProperty("dataRev").GetInt64());
                expected = reset.GetProperty("data").Clone();
                Assert.Equal("reset", expected.GetProperty("summary").GetString());

                var schemaFree = await Apply(KernelMcpToolNames.TrackSet,
                    new { source = "export default defineTemplate({ id: 'no-schema', title: 'No Schema' });" });
                Assert.Equal(3, schemaFree.GetProperty("dataRev").GetInt64());
                Assert.True(JsonElement.DeepEquals(expected, schemaFree.GetProperty("data")));

                var cleared = await Apply(KernelMcpToolNames.TrackSet, new { clear = true });
                Assert.Equal(3, cleared.GetProperty("dataRev").GetInt64());
                Assert.True(JsonElement.DeepEquals(expected, cleared.GetProperty("data")));
            }

            await using var reopened = NewHost(directory.FullName);
            var persisted = (await reopened.Services.GetRequiredService<IPergaKernel>().GetTrackAggregateAsync(id)).Track;
            Assert.Null(persisted.TemplateId);
            Assert.Equal(3, persisted.DataRev);
            Assert.True(JsonElement.DeepEquals(expected, persisted.Data));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("equity-research", "weekly-price-refresh", "30 8 * * 1", "America/New_York", "ticker", "ACME")]
    [InlineData("okr-loop", "daily-ci-check", "0 9 * * *", "Asia/Shanghai", "checkCmd", "collect-ci-metrics")]
    public async Task TemplatePackagesMaterializeRecurringSchedules(
        string template, string scheduleId, string cron, string timezone, string inputKey, string inputValue)
    {
        await using var host = NewHost();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../templates", template));
        kernel.Templates.RegisterTemplate(Perga.PolicyVm.TemplateBundle.PackDirectory(package));
        var area = await kernel.CreateAreaAsync(new("Schedules", DefaultCwd: host.DataDir));
        var created = await kernel.CreateTrackAsync(new(area.Id, TemplateId: template,
            TemplateInputs: JsonSerializer.SerializeToElement(new Dictionary<string, string> { [inputKey] = inputValue })));

        var schedule = Assert.Single((await kernel.GetTrackFlowAsync(created.Track.Id)).Projection.Nodes);
        Assert.Equal(scheduleId, schedule.Id);
        Assert.Equal("template", schedule.Source);
        Assert.Equal(cron, schedule.Cron);
        Assert.Equal(timezone, schedule.Tz);
        Assert.Contains(inputValue, schedule.Prompt);
        Assert.NotNull(schedule.NextFireAtMs);
    }

    [Fact]
    public async Task InputUpdateRejectsIncompatibleSchemaAndStoreFencesStaleDataRevision()
    {
        await using var host = NewHost();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        const string inputDependent = """
            export default defineTemplate({
              id: 'input-gated', title: 'Input Gated',
              inputs: { numeric: { type: 'boolean', default: false } },
              setup(inputs) {
                return {
                  data: {
                    schema: t => ({ value: inputs.numeric ? t.number() : t.string() }),
                    initial: () => ({ value: inputs.numeric ? 42 : 'saved' })
                  }
                };
              }
            });
            """;
        kernel.Templates.RegisterTemplate(inputDependent);
        var area = await kernel.CreateAreaAsync(new("Inputs", DefaultCwd: host.DataDir));
        var created = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "input-gated"))).Track;

        using var badInput = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{created.Id}/template/inputs", new { numeric = true });
        Assert.Equal(HttpStatusCode.Conflict, badInput.StatusCode);
        var afterFailedInput = (await kernel.GetTrackAggregateAsync(created.Id)).Track;
        Assert.False(afterFailedInput.TemplateInputs.TryGetProperty("numeric", out var num) && num.GetBoolean());

        // Direct store CAS fence rejects stale writer trying to commit DataRev=1 after rev already advanced to 1.
        var fresh = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{created.Id}/data",
            new { expectedDataRev = 0, patch = Parse("""{"value":"updated"}""") });
        fresh.EnsureSuccessStatusCode();
        var staleCandidate = created with { Data = Parse("""{"value":"stale-overwrite"}"""), DataRev = 1 };
        var conflict = await Assert.ThrowsAsync<KernelException>(() =>
            store.UpdateTrackAndEmitAsync(staleCandidate, evt: null, CancellationToken.None, writeData: true));
        Assert.Equal(KernelErrorCode.Conflict, conflict.Code);
        var current = (await kernel.GetTrackAggregateAsync(created.Id)).Track;
        Assert.Equal(1, current.DataRev);
        Assert.Equal("updated", current.Data.GetProperty("value").GetString());

        // Input updates validate the effective schema even when Track.Data is empty (metadata-only).
        const string emptyDataTemplate = """
            export default defineTemplate({
              id: 'empty-input-gated', title: 'Empty Input Gated',
              inputs: { mode: { type: 'string', default: 'optional' } },
              setup(inputs) {
                return {
                  data: {
                    schema: t => inputs.mode === 'invalid'
                      ? { label: 'not-a-schema' }
                      : { label: inputs.mode === 'required' ? t.string() : t.string().optional() },
                    initial: () => ({})
                  }
                };
              }
            });
            """;
        kernel.Templates.RegisterTemplate(emptyDataTemplate);
        var emptyTrack = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "empty-input-gated"))).Track;
        using var reqMode = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{emptyTrack.Id}/template/inputs", new { mode = "required" });
        Assert.Equal(HttpStatusCode.Conflict, reqMode.StatusCode);
        using var badMode = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{emptyTrack.Id}/template/inputs", new { mode = "invalid" });
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
    }

    [Fact]
    public async Task LargeEquityResearchEventHistorySupportsKeyedUpsertAndNoopRetry()
    {
        await using var host = NewHost();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../templates/equity-research"));
        kernel.Templates.RegisterTemplate(Perga.PolicyVm.TemplateBundle.PackDirectory(package));
        var area = await kernel.CreateAreaAsync(new("Research", DefaultCwd: host.DataDir));
        var track = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "equity-research"))).Track;
        var url = $"/api/v1/tracks/{track.Id}/data";

        var seedEvents = Enumerable.Range(1, 400).Select(i => new
        {
            id = $"evt-{i:D3}",
            date = "2026-01-15",
            title = $"Event {i}",
            verdict = "inline",
            summary = "Quarterly check",
            expects = Array.Empty<object>(),
        }).ToArray();

        using var seeded = await host.AuthenticatedClient.PatchAsJsonAsync(url, new { expectedDataRev = 0, patch = new { events = seedEvents } });
        seeded.EnsureSuccessStatusCode();

        using var updated = await host.AuthenticatedClient.PatchAsJsonAsync(url, new
        {
            expectedDataRev = 1,
            patch = new { events = new Dictionary<string, object> { ["$upsert"] = new[] { new { id = "evt-200", title = "Event 200 Revised" } } } }
        });
        updated.EnsureSuccessStatusCode();
        var updatedBody = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, updatedBody.GetProperty("dataRev").GetInt64());
        Assert.Equal("Event 200 Revised", updatedBody.GetProperty("data").GetProperty("events")[199].GetProperty("title").GetString());

        using var noop = await host.AuthenticatedClient.PatchAsJsonAsync(url, new
        {
            expectedDataRev = 2,
            patch = new { events = new Dictionary<string, object> { ["$upsert"] = new[] { new { id = "evt-200", title = "Event 200 Revised" } } } }
        });
        noop.EnsureSuccessStatusCode();
        var noopBody = await noop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, noopBody.GetProperty("dataRev").GetInt64());
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static PergaHostTestCluster NewHost(string? directory = null) =>
        new(explicitDataDir: directory, configureHostOptions: options =>
        {
            options.EnableScheduleService = false;
            options.EnableMaintenanceService = false;
        });
}
