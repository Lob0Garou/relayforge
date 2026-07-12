using RelayForge.Domain.Events;
using RelayForge.Domain.Endpoints;
using Xunit;
using System.Globalization;

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

    [Theory]
    [InlineData("{\"s\":\"caf\\u00e9\"}", "{\"s\":\"café\"}")]
    [InlineData("{\"n\":-0}", "{\"n\":0}")]
    [InlineData("{\"n\":123456789012345678901234567890}", "{\"n\":123456789012345678901234567890.0}")]
    [InlineData("{\"n\":1e999999999999999999999}", "{\"n\":10e999999999999999999998}")]
    public void Canonicalizer_preserves_exact_semantics_for_edge_cases(string left, string right)
    {
        var endpointId = new WebhookEndpointId(Guid.NewGuid());
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("pt-BR");
            var first = EventFingerprint.Create(endpointId, "x", left); var second = EventFingerprint.Create(endpointId, "x", right);
            Assert.True(first.TryGetValue(out var a)); Assert.True(second.TryGetValue(out var b)); Assert.Equal(a, b);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Theory]
    [InlineData("{\"outer\":{\"x\":1,\"x\":2}}")]
    [InlineData("[{\"x\":1,\"x\":2}]")]
    public void Canonicalizer_rejects_nested_duplicate_properties(string payload)
    {
        var result = EventFingerprint.Create(new WebhookEndpointId(Guid.NewGuid()), "x", payload);
        Assert.False(result.TryGetValue(out _)); Assert.Equal("event.payload_invalid", result.Error.Code);
    }
}
