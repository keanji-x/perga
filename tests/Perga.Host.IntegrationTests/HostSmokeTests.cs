using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Perga.Host.IntegrationTests;

public sealed class HostSmokeTests
{
    [Fact]
    public async Task HealthzEndpoint_ReturnsOk()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
