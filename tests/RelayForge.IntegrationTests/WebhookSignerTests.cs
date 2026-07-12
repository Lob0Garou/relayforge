using System.Text;
using RelayForge.Infrastructure.Security;

namespace RelayForge.IntegrationTests;

public sealed class WebhookSignerTests
{
    private const string DeliveryId = "018d2f54-31ec-7d8a-a7da-4f9ed11c2e21";
    private static readonly byte[] Secret = "test-secret"u8.ToArray();
    private static readonly byte[] Payload = "{\"orderId\":42}"u8.ToArray();

    [Fact]
    public void Sign_matches_literal_protocol_vector()
    {
        var signature = WebhookSigner.Sign(Secret, 1_700_000_000, DeliveryId, Payload);

        Assert.Equal("sha256=6cf268d9cf0cafd3ab7994ea4945d753a11c2b28faf0b02d034bbb3ee6df847a", signature);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("deliveryId")]
    [InlineData("timestamp")]
    public void Verify_rejects_any_change_to_signed_frame(string changedField)
    {
        var time = new FrozenTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var signature = WebhookSigner.Sign(Secret, 1_700_000_000, DeliveryId, Payload);
        var payload = changedField == "payload" ? "{\"orderId\":43}"u8.ToArray() : Payload;
        var deliveryId = changedField == "deliveryId" ? $"{DeliveryId}-changed" : DeliveryId;
        var timestamp = changedField == "timestamp" ? 1_700_000_001 : 1_700_000_000;

        Assert.False(WebhookSigner.Verify(Secret, timestamp, deliveryId, payload, signature, TimeSpan.FromMinutes(5), time));
    }

    [Theory]
    [InlineData(1_699_999_699)]
    [InlineData(1_700_000_301)]
    public void Verify_rejects_stale_or_future_timestamp(long timestamp)
    {
        var time = new FrozenTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var signature = WebhookSigner.Sign(Secret, timestamp, DeliveryId, Payload);

        Assert.False(WebhookSigner.Verify(Secret, timestamp, DeliveryId, Payload, signature, TimeSpan.FromMinutes(5), time));
    }

    [Theory]
    [InlineData("")]
    [InlineData("aaf063e8181978f33c375dfbe22e269a9eb47c906adf64695f9493233e635ff3")]
    [InlineData("sha256=xyz")]
    [InlineData("sha256=6CF268D9CF0CAFD3AB7994EA4945D753A11C2B28FAF0B02D034BBB3EE6DF847A")]
    [InlineData("sha512=aaf063e8181978f33c375dfbe22e269a9eb47c906adf64695f9493233e635ff3")]
    public void Verify_rejects_malformed_signature(string signature)
    {
        var time = new FrozenTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

        Assert.False(WebhookSigner.Verify(Secret, 1_700_000_000, DeliveryId, Payload, signature, TimeSpan.FromMinutes(5), time));
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
