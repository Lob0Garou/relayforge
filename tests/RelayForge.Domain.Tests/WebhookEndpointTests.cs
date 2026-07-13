using RelayForge.Domain.Endpoints;
using Xunit;

namespace RelayForge.Domain.Tests;

public sealed class WebhookEndpointTests
{
    [Fact]
    public void Create_rejects_empty_name()
    {
        var result = WebhookEndpoint.Create(" ", "https://example.com/hooks", TimeSpan.FromSeconds(10), "protected");
        Assert.False(result.IsSuccess);
        Assert.Equal("endpoint.name_required", result.Error.Code);
    }

    [Fact]
    public void Create_rejects_non_http_url()
    {
        var result = WebhookEndpoint.Create("Orders", "ftp://example.com/hooks", TimeSpan.FromSeconds(10), "protected");
        Assert.False(result.IsSuccess);
        Assert.Equal("endpoint.url_invalid", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Create_rejects_timeout_outside_explicit_limits(int seconds)
    {
        var result = WebhookEndpoint.Create("Orders", "https://example.com/hooks", TimeSpan.FromSeconds(seconds), "protected");
        Assert.False(result.IsSuccess);
        Assert.Equal("endpoint.timeout_invalid", result.Error.Code);
    }

    [Fact]
    public void Create_returns_valid_immutable_endpoint()
    {
        var result = WebhookEndpoint.Create(" Orders ", "https://example.com/hooks", TimeSpan.FromSeconds(30), "protected");
        Assert.True(result.IsSuccess);
        Assert.True(result.TryGetValue(out var endpoint));
        Assert.NotEqual(Guid.Empty, endpoint.Id.Value);
        Assert.Equal("Orders", endpoint.Name);
        Assert.Equal(new Uri("https://example.com/hooks"), endpoint.Url);
        Assert.Equal(TimeSpan.FromSeconds(30), endpoint.Timeout);
        Assert.True(endpoint.IsActive);
        Assert.Equal("protected", endpoint.ProtectedSecret);
    }
}
