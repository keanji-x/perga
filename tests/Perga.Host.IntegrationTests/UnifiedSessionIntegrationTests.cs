using System.Net;
using System.Net.Http.Json;
using Perga.Host.IntegrationTests.Fixtures;

namespace Perga.Host.IntegrationTests;

public sealed class UnifiedSessionIntegrationTests
{
    [Fact]
    public async Task SessionCreationRejectsRetiredTaskArgument()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, _) = await host.CreateTrackAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tracks/{trackId}/sessions")
        {
            Content = JsonContent.Create(new { prompt = "Inspect", task = new { } })
        };
        request.Headers.Add("Idempotency-Key", "retired-task");
        using var response = await host.AuthenticatedClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
