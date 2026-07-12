using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayForge.Domain.Events;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class EventApiTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Post_and_replays_are_atomic_and_idempotent()
    {
        await fixture.ResetAsync(); var endpointId = await CreateEndpointAsync();
        using var client = fixture.Factory.CreateClient();
        using var first = await PostAsync(client, "key-1", endpointId, "order.created", "{\"b\":2,\"a\":1}");
        using var replay = await PostAsync(client, "key-1", endpointId, "order.created", "{ \"a\": 1, \"b\": 2 }");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode); Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<EventResponse>(); var b = await replay.Content.ReadFromJsonAsync<EventResponse>();
        Assert.NotNull(a); Assert.Equal(a, b); Assert.Equal($"/api/events/{a.EventId}", first.Headers.Location?.OriginalString);
        await AssertCountsAsync(1, 1);
    }

    [Theory]
    [InlineData("order.changed", "{\"a\":1}")]
    [InlineData("order.created", "{\"a\":2}")]
    public async Task Reusing_global_key_with_different_content_returns_conflict(string type, string payload)
    {
        await fixture.ResetAsync(); var endpointId = await CreateEndpointAsync(); using var client = fixture.Factory.CreateClient();
        using var first = await PostAsync(client, "conflict", endpointId, "order.created", "{\"a\":1}"); first.EnsureSuccessStatusCode();
        using var response = await PostAsync(client, "conflict", endpointId, type, payload);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task Twenty_concurrent_posts_create_one_event_and_delivery()
    {
        await fixture.ResetAsync(); var endpointId = await CreateEndpointAsync(); using var client = fixture.Factory.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => PostAsync(client, "race", endpointId, "order.created", "{\"a\":1}")));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<EventResponse>()));
        Assert.Single(bodies.Select(x => x!.EventId).Distinct()); Assert.Single(bodies.Select(x => x!.DeliveryId).Distinct());
        foreach (var response in responses) response.Dispose(); await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task Reusing_global_key_for_another_endpoint_returns_conflict()
    {
        await fixture.ResetAsync(); var firstEndpoint = await CreateEndpointAsync(); var secondEndpoint = await CreateEndpointAsync(); using var client = fixture.Factory.CreateClient();
        using var first = await PostAsync(client, "global-key", firstEndpoint, "x", "{}"); first.EnsureSuccessStatusCode();
        using var response = await PostAsync(client, "global-key", secondEndpoint, "x", "{}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); await AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task Replay_returns_original_ids_after_endpoint_is_deactivated()
    {
        await fixture.ResetAsync(); var endpointId = await CreateEndpointAsync(); using var client = fixture.Factory.CreateClient();
        using var first = await PostAsync(client, "stable-replay", endpointId, "x", "{}"); var original = await first.Content.ReadFromJsonAsync<EventResponse>();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_endpoints SET is_active = false WHERE id = {endpointId}");
        using var replay = await PostAsync(client, "stable-replay", endpointId, "x", "{}");
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode); Assert.Equal(original, await replay.Content.ReadFromJsonAsync<EventResponse>());
    }

    [Fact]
    public async Task Invalid_duplicate_json_missing_key_unknown_and_inactive_endpoint_are_rejected()
    {
        await fixture.ResetAsync(); var endpointId = await CreateEndpointAsync(); using var client = fixture.Factory.CreateClient();
        using var missing = await client.PostAsJsonAsync("/api/events", new { endpointId, eventType = "x", payload = new { a = 1 } }); Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var duplicate = new HttpRequestMessage(HttpMethod.Post, "/api/events") { Content = new StringContent($"{{\"endpointId\":\"{endpointId}\",\"eventType\":\"x\",\"payload\":{{\"a\":1,\"a\":2}}}}", Encoding.UTF8, "application/json") }; duplicate.Headers.Add("Idempotency-Key", "dup");
        using var duplicateResponse = await client.SendAsync(duplicate); Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);
        using var unknown = await PostAsync(client, "unknown", Guid.NewGuid(), "x", "{}"); Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_endpoints SET is_active = false WHERE id = {endpointId}");
        using var inactive = await PostAsync(client, "inactive", endpointId, "x", "{}"); Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
    }

    [Fact]
    public async Task Blank_or_oversized_idempotency_keys_are_rejected()
    {
        await fixture.ResetAsync(); var endpointId = await CreateEndpointAsync(); using var client = fixture.Factory.CreateClient();
        using var blank = await PostAsync(client, "   ", endpointId, "x", "{}");
        using var oversized = await PostAsync(client, new string('k', 201), endpointId, "x", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
        await AssertCountsAsync(0, 0);
    }

    private async Task<Guid> CreateEndpointAsync() { using var client = fixture.Factory.CreateClient(); using var response = await client.PostAsJsonAsync("/api/endpoints", new { name="Events", url="https://example.com/hook", timeoutSeconds=30 }); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<EndpointResponse>())!.Id; }
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string key, Guid endpointId, string eventType, string payload) { using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events") { Content = new StringContent($"{{\"endpointId\":\"{endpointId}\",\"eventType\":{System.Text.Json.JsonSerializer.Serialize(eventType)},\"payload\":{payload}}}", Encoding.UTF8, "application/json") }; request.Headers.Add("Idempotency-Key", key); return await client.SendAsync(request); }
    private async Task AssertCountsAsync(int events, int deliveries) { await using var scope = fixture.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>(); Assert.Equal(events, await db.IncomingEvents.CountAsync()); Assert.Equal(deliveries, await db.Deliveries.CountAsync()); }
    private sealed record EndpointResponse(Guid Id);
    private sealed record EventResponse(Guid EventId, Guid DeliveryId, string State);
}
