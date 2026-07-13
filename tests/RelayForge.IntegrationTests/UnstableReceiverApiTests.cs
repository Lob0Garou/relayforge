using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using RelayForge.Infrastructure.Security;

namespace RelayForge.IntegrationTests;

public sealed class UnstableReceiverApiTests : IClassFixture<UnstableReceiverFactory>
{
    private const string Secret = "integration-test-secret";
    private readonly HttpClient _client;

    public UnstableReceiverApiTests(UnstableReceiverFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Valid_signed_webhook_is_accepted()
    {
        await ConfigureScenarioAsync(0);
        var response = await SendWebhookAsync(Guid.NewGuid(), "{\"ok\":true}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_signature_is_unauthorized()
    {
        var request = CreateRequest(Guid.NewGuid(), "{}"u8.ToArray(), "sha256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_timestamp_is_unauthorized()
    {
        var deliveryId = Guid.NewGuid();
        var payload = "{}"u8.ToArray();
        var staleTimestamp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Encoding.UTF8.GetBytes(Secret), staleTimestamp, deliveryId, payload);
        var request = CreateRequest(deliveryId, payload, signature, staleTimestamp);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Configured_receiver_fails_n_times_then_succeeds_and_reports_state()
    {
        var deliveryId = Guid.NewGuid();
        var configured = await ConfigureScenarioAsync(2);

        var first = await SendWebhookAsync(deliveryId, "{\"attempt\":1}");
        var second = await SendWebhookAsync(deliveryId, "{\"attempt\":1}");
        var third = await SendWebhookAsync(deliveryId, "{\"attempt\":1}");
        var state = await _client.GetFromJsonAsync<ReceiverState>($"/operations/deliveries/{deliveryId}");

        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(3, state?.Attempts);
        Assert.Equal(2, state?.FailuresBeforeSuccess);
    }

    [Theory]
    [InlineData("a./b", "00000000-0000-0000-0000-000000000000")]
    [InlineData("a/.b", "00000000-0000-0000-0000-000000000000")]
    [InlineData("{018d2f54-31ec-7d8a-a7da-4f9ed11c2e21}", "018d2f54-31ec-7d8a-a7da-4f9ed11c2e21")]
    [InlineData("018D2F54-31EC-7D8A-A7DA-4F9ED11C2E21", "018d2f54-31ec-7d8a-a7da-4f9ed11c2e21")]
    public async Task Noncanonical_or_ambiguous_delivery_id_is_unauthorized(string deliveryId, string underlyingDeliveryId)
    {
        var request = CreateRawIdRequest(deliveryId, Guid.ParseExact(underlyingDeliveryId, "D"), "{}"u8.ToArray());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_required_header_is_unauthorized()
    {
        var deliveryId = Guid.NewGuid();
        var payload = "{}"u8.ToArray();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var validSignature = WebhookSigner.Sign(Encoding.UTF8.GetBytes(Secret), timestamp, deliveryId, payload);
        var request = CreateRequest(deliveryId, payload, validSignature, timestamp);
        request.Headers.Add("RelayForge-Signature", "sha256=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reconfiguring_scenario_atomically_resets_attempt_counters()
    {
        var deliveryId = Guid.NewGuid();
        await ConfigureScenarioAsync(1);
        await SendWebhookAsync(deliveryId, "{}");

        await ConfigureScenarioAsync(0);
        var oldState = await _client.GetAsync($"/operations/deliveries/{deliveryId:D}");
        var next = await SendWebhookAsync(deliveryId, "{}");

        Assert.Equal(HttpStatusCode.NotFound, oldState.StatusCode);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Invalid_scenario_is_bad_request()
    {
        var response = await _client.PutAsJsonAsync("/operations/scenario", new
        {
            failuresBeforeSuccess = 101,
            failureStatusCode = 399,
            delayMilliseconds = 30_001
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Body_at_limit_is_accepted_and_content_length_over_limit_is_rejected()
    {
        await ConfigureScenarioAsync(0);
        var accepted = await SendWebhookAsync(Guid.NewGuid(), new byte[65_536]);
        var rejected = await SendWebhookAsync(Guid.NewGuid(), new byte[65_537]);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
    }

    [Fact]
    public async Task Chunked_body_over_limit_is_rejected()
    {
        await ConfigureScenarioAsync(0);
        var deliveryId = Guid.NewGuid();
        var payload = new byte[65_537];
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Encoding.UTF8.GetBytes(Secret), timestamp, deliveryId, payload);
        var request = CreateRequest(deliveryId, payload, signature, timestamp);
        request.Content = new StreamingContent(payload);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_attempts_are_counted_without_loss()
    {
        await ConfigureScenarioAsync(100);
        var deliveryId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SendWebhookAsync(deliveryId, "{}")));
        var state = await _client.GetFromJsonAsync<ReceiverState>($"/operations/deliveries/{deliveryId:D}");

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode));
        Assert.Equal(20, state?.Attempts);
    }

    private Task<HttpResponseMessage> SendWebhookAsync(Guid deliveryId, string payload) =>
        SendWebhookAsync(deliveryId, Encoding.UTF8.GetBytes(payload));

    private Task<HttpResponseMessage> SendWebhookAsync(Guid deliveryId, byte[] payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Encoding.UTF8.GetBytes(Secret), timestamp, deliveryId, payload);
        return _client.SendAsync(CreateRequest(deliveryId, payload, signature, timestamp));
    }

    private Task<HttpResponseMessage> ConfigureScenarioAsync(int failuresBeforeSuccess) =>
        _client.PutAsJsonAsync("/operations/scenario", new
        {
            failuresBeforeSuccess,
            failureStatusCode = 503,
            delayMilliseconds = 0
        });

    private static HttpRequestMessage CreateRequest(Guid deliveryId, byte[] payload, string signature, long? timestamp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/relayforge")
        {
            Content = new ByteArrayContent(payload)
        };
        request.Headers.Add("RelayForge-Delivery-Id", deliveryId.ToString("D"));
        request.Headers.Add("RelayForge-Timestamp", (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("RelayForge-Signature", signature);
        return request;
    }

    private static HttpRequestMessage CreateRawIdRequest(string deliveryId, Guid underlyingDeliveryId, byte[] payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Encoding.UTF8.GetBytes(Secret), timestamp, underlyingDeliveryId, payload);
        var request = CreateRequest(underlyingDeliveryId, payload, signature, timestamp);
        request.Headers.Remove("RelayForge-Delivery-Id");
        request.Headers.Add("RelayForge-Delivery-Id", deliveryId);
        return request;
    }

    private sealed record ReceiverState(int Attempts, int FailuresBeforeSuccess);

    private sealed class StreamingContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

public sealed class UnstableReceiverSecretControlTests : IClassFixture<UnstableReceiverFactory>
{
    private readonly UnstableReceiverFactory _factory;
    public UnstableReceiverSecretControlTests(UnstableReceiverFactory factory) => _factory = factory;

    [Fact]
    public async Task Secret_control_is_absent_outside_development()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync("/control/secret", new { secret = "replacement-secret-123" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task Secret_control_rejects_out_of_bounds_values(string secret)
    {
        using var client = DevelopmentClient();
        var response = await client.PutAsJsonAsync("/control/secret", new { secret });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rotating_secret_accepts_new_signature_and_rejects_old_signature()
    {
        const string replacement = "replacement-secret-123";
        using var client = DevelopmentClient();
        var rotated = await client.PutAsJsonAsync("/control/secret", new { secret = replacement });
        var deliveryId = Guid.NewGuid();
        var payload = "{}"u8.ToArray();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var oldResponse = await client.SendAsync(SignedRequest(deliveryId, payload, timestamp, "integration-test-secret"));
        var newResponse = await client.SendAsync(SignedRequest(Guid.NewGuid(), payload, timestamp, replacement));

        Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newResponse.StatusCode);
    }

    private HttpClient DevelopmentClient() => _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development")).CreateClient();

    private static HttpRequestMessage SignedRequest(Guid id, byte[] payload, long timestamp, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/relayforge") { Content = new ByteArrayContent(payload) };
        request.Headers.Add("RelayForge-Delivery-Id", id.ToString("D"));
        request.Headers.Add("RelayForge-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("RelayForge-Signature", WebhookSigner.Sign(Encoding.UTF8.GetBytes(secret), timestamp, id, payload));
        return request;
    }
}

public sealed class UnstableReceiverFactory : WebApplicationFactory<RelayForge.UnstableReceiver.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Receiver:SigningSecret"] = "integration-test-secret",
            ["Receiver:TimestampToleranceSeconds"] = "300",
            ["Receiver:MaxBodyBytes"] = "65536"
        }));
    }
}
