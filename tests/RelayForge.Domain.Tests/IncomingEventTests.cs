using RelayForge.Domain.Events;
using RelayForge.Domain.Endpoints;
using Xunit;

namespace RelayForge.Domain.Tests;

public sealed class IncomingEventTests
{
    [Fact]
    public void Create_establishes_pending_event_and_delivery()
    {
        var endpointId = new WebhookEndpointId(Guid.NewGuid());
        var result = IncomingEvent.Create(endpointId, "order.created", "{\"a\":1}", "key", new string('a', 64));

        Assert.True(result.TryGetValue(out var incomingEvent));
        Assert.Equal(EventStatus.Pending, incomingEvent.Status);
        Assert.Equal(DeliveryStatus.Pending, incomingEvent.Delivery.Status);
        Assert.Equal(incomingEvent.Id, incomingEvent.Delivery.EventId);
        Assert.Equal(endpointId, incomingEvent.Delivery.EndpointId);
    }

    [Theory]
    [InlineData("{\"b\":2,\"a\":1}", "{ \"a\" : 1, \"b\" : 2 }")]
    [InlineData("{\"a\":[2,1],\"x\":true}", "{\"x\":true,\"a\":[2,1]}")]
    [InlineData("{\"n\":1}", "{\"n\":1.0}")]
    [InlineData("{\"n\":1e0}", "{\"n\":0.100e1}")]
    public void Canonicalizer_produces_same_fingerprint_for_semantically_equivalent_json(string left, string right)
    {
        var endpointId = new WebhookEndpointId(Guid.NewGuid());
        var first = EventFingerprint.Create(endpointId, "order.created", left);
        var second = EventFingerprint.Create(endpointId, "order.created", right);
        Assert.True(first.TryGetValue(out var a));
        Assert.True(second.TryGetValue(out var b));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Canonicalizer_rejects_duplicate_properties()
    {
        var result = EventFingerprint.Create(new WebhookEndpointId(Guid.NewGuid()), "order.created", "{\"a\":1,\"a\":2}");
        Assert.False(result.TryGetValue(out _));
        Assert.Equal("event.payload_invalid", result.Error.Code);
    }
}
