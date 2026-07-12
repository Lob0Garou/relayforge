using System.Net;

namespace RelayForge.IntegrationTests;

public sealed class HealthCheckTests(RelayForgeApiFactory factory) : IClassFixture<RelayForgeApiFactory>
{
    [Fact]
    public async Task Health_endpoint_returns_healthy()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
