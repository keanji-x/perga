using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;

namespace Perga.Host.IntegrationTests;

public sealed class SessionModelTests
{
    [Fact]
    public async Task ModelEndpointPersistsAcrossRestartAndSupportsNull()
    {
        var directory = Directory.CreateTempSubdirectory("perga-model-");
        try
        {
            string trackId;
            string sessionId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                using var response = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model = "chosen-model" });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var session = await response.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions);
                Assert.Equal("chosen-model", session!.Model);
                using var missing = await host.Client.PutAsJsonAsync("/api/v1/sessions/missing/model", new { model = "chosen-model" });
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var kernel = reopened.Services.GetRequiredService<IPergaKernel>();
            Assert.Equal("chosen-model", (await kernel.ListSessionsAsync(trackId)).Single(s => s.Id == sessionId).Model);
            using var reset = await reopened.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model = (string?)null });
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            var cleared = await reset.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions);
            Assert.Null(cleared!.Model);
            Assert.Null((await kernel.ListSessionsAsync(trackId)).Single(s => s.Id == sessionId).Model);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
