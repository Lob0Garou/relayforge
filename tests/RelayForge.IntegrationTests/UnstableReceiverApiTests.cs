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
        var response = await SendWebhookAsync(Guid.NewGuid().ToString(), "{\"ok\":true}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_signature_is_unauthorized()
    {
        var request = CreateRequest(Guid.NewGuid().ToString(), "{}", "sha256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_timestamp_is_unauthorized()
    {
        var deliveryId = Guid.NewGuid().ToString();
        var payload = "{}";
        var staleTimestamp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Encoding.UTF8.GetBytes(Secret), staleTimestamp, deliveryId, Encoding.UTF8.GetBytes(payload));
        var request = CreateRequest(deliveryId, payload, signature, staleTimestamp);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Configured_receiver_fails_n_times_then_succeeds_and_reports_state()
    {
        var deliveryId = Guid.NewGuid().ToString();
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

    private Task<HttpResponseMessage> SendWebhookAsync(string deliveryId, string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = WebhookSigner.Sign(Encoding.UTF8.GetBytes(Secret), timestamp, deliveryId, Encoding.UTF8.GetBytes(payload));
        return _client.SendAsync(CreateRequest(deliveryId, payload, signature, timestamp));
    }

    private Task<HttpResponseMessage> ConfigureScenarioAsync(int failuresBeforeSuccess) =>
        _client.PutAsJsonAsync("/operations/scenario", new
        {
            failuresBeforeSuccess,
            failureStatusCode = 503,
            delayMilliseconds = 0
        });

    private static HttpRequestMessage CreateRequest(string deliveryId, string payload, string signature, long? timestamp = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/relayforge")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RelayForge-Delivery-Id", deliveryId);
        request.Headers.Add("RelayForge-Timestamp", (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("RelayForge-Signature", signature);
        return request;
    }

    private sealed record ReceiverState(int Attempts, int FailuresBeforeSuccess);
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
