using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayForge.Domain.Endpoints;
using RelayForge.Domain.Events;
using RelayForge.Domain.Retry;
using RelayForge.Infrastructure.Delivery;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class OperationsApiTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Operations_contracts_are_bounded_ordered_and_do_not_leak_sensitive_data()
    {
        var (endpoint, incoming) = await SeedDeadLetterAsync();
        using var client = fixture.Factory.CreateClient();

        using var overview = await client.GetAsync("/api/operations/overview");
        using var events = await client.GetAsync($"/api/events?page=1&pageSize=9999&type=secret.event&status=DeadLettered&endpointId={endpoint.Id.Value}");
        using var detail = await client.GetAsync($"/api/deliveries/{incoming.Delivery.Id.Value}");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var text = string.Join('\n', await overview.Content.ReadAsStringAsync(), await events.Content.ReadAsStringAsync(), await detail.Content.ReadAsStringAsync());
        Assert.DoesNotContain("private-payload", text);
        Assert.DoesNotContain("never-expose-me", text);
        Assert.DoesNotContain("receiver.invalid", text);
        Assert.DoesNotContain("idempotency", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("safe snippet", text);
        Assert.Contains("\"pageSize\":100", await events.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Event_and_dead_letter_pagination_rejects_extreme_pages()
    {
        using var client = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/events?page=1001&pageSize=20")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/dead-letters?page=1001&pageSize=20")).StatusCode);
    }

    [Fact]
    public async Task Operations_endpoints_contract_is_bounded_and_omits_destination_and_secrets()
    {
        var (endpoint, _) = await SeedEventAsync(deadLetter: false);
        using var response = await fixture.Factory.CreateClient().GetAsync("/api/operations/endpoints?page=1&pageSize=9999");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(100, root.GetProperty("pageSize").GetInt32());
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(endpoint.Id.Value, item.GetProperty("id").GetGuid());
        Assert.Equal("Receiver", item.GetProperty("name").GetString());
        Assert.Equal(30, item.GetProperty("timeoutSeconds").GetInt32());
        Assert.DoesNotContain("receiver.invalid", json);
        Assert.DoesNotContain("never-expose-me", json);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delivery_detail_bounds_history_and_never_returns_response_snippets()
    {
        var (_, incoming) = await SeedDeadLetterAsync(); var id = incoming.Delivery.Id.Value;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO delivery_attempts ("Id", "DeliveryId", "Number", "StartedAt", "CompletedAt", "DurationMilliseconds", "Outcome", "HttpStatusCode", "Error", response_snippet)
                SELECT gen_random_uuid(), {id}, n, clock_timestamp(), clock_timestamp(), 0, 'Failed', 500, 'bounded_error', 'snippet-sentinel'
                FROM generate_series(2, 106) n
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO delivery_replays ("Id", "DeliveryId", requested_at, starting_attempt_number, cycle_number)
                SELECT gen_random_uuid(), {id}, clock_timestamp() + n * interval '1 second', n, n FROM generate_series(1, 55) n
                """);
        }
        using var response = await fixture.Factory.CreateClient().GetAsync($"/api/deliveries/{id}");
        var json = await response.Content.ReadAsStringAsync(); var document = JsonDocument.Parse(json); var root = document.RootElement;
        Assert.Equal(100, root.GetProperty("attempts").GetArrayLength()); Assert.Equal(50, root.GetProperty("replays").GetArrayLength());
        Assert.True(root.GetProperty("historyTruncated").GetBoolean()); Assert.DoesNotContain("snippet-sentinel", json); Assert.DoesNotContain("responseSnippet", json);
        Assert.Equal(106, root.GetProperty("attempts")[0].GetProperty("number").GetInt32()); Assert.Equal(55, root.GetProperty("replays")[0].GetProperty("cycleNumber").GetInt32());
    }

    [Fact]
    public async Task Concurrent_replay_creates_one_audit_record_and_preserves_attempt_history()
    {
        var (_, incoming) = await SeedDeadLetterAsync();
        using var client = fixture.Factory.CreateClient();

        var responses = await Task.WhenAll(
            client.PostAsync($"/api/dead-letters/{incoming.Delivery.Id.Value}/replay", null),
            client.PostAsync($"/api/dead-letters/{incoming.Delivery.Id.Value}/replay", null));

        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        var accepted = responses.Single(x => x.StatusCode == HttpStatusCode.Accepted);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("replayId").GetGuid());
        Assert.Equal("Replayed", body.GetProperty("status").GetString());
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>();
        Assert.Equal(1, await db.DeliveryReplays.CountAsync());
        Assert.Equal(1, await db.DeliveryAttempts.CountAsync());
        var lease = Assert.Single(await scope.ServiceProvider.GetRequiredService<DeliveryLeaseRepository>().ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        Assert.Equal(2, lease.AttemptNumber);
    }

    [Fact]
    public async Task Replay_rejects_not_found_non_dlq_and_inactive_endpoint()
    {
        await fixture.ResetAsync();
        using var client = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/dead-letters/{Guid.NewGuid()}/replay", null)).StatusCode);
        var (endpoint, incoming) = await SeedEventAsync(deadLetter: false);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/dead-letters/{incoming.Delivery.Id.Value}/replay", null)).StatusCode);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_endpoints SET is_active = false WHERE id = {endpoint.Id.Value}");
        await DeadLetterAsync(incoming.Delivery.Id.Value);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/dead-letters/{incoming.Delivery.Id.Value}/replay", null)).StatusCode);
    }

    private async Task<(WebhookEndpoint Endpoint, IncomingEvent Event)> SeedDeadLetterAsync()
    {
        var value = await SeedEventAsync(deadLetter: false);
        await DeadLetterAsync(value.Event.Delivery.Id.Value);
        return value;
    }

    private async Task<(WebhookEndpoint Endpoint, IncomingEvent Event)> SeedEventAsync(bool deadLetter)
    {
        await fixture.ResetAsync();
        var protector = fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RelayForge.WebhookEndpointSecrets.v1");
        var endpointResult = WebhookEndpoint.Create("Receiver", "http://receiver.invalid/hook", TimeSpan.FromSeconds(30), protector.Protect("never-expose-me"));
        Assert.True(endpointResult.TryGetValue(out var endpoint));
        var eventResult = IncomingEvent.Create(endpoint.Id, "secret.event", "{\"value\":\"private-payload\"}", Guid.NewGuid().ToString("N"), new string('a', 64));
        Assert.True(eventResult.TryGetValue(out var incoming));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>();
        db.Add(endpoint); db.Add(incoming); await db.SaveChangesAsync();
        if (deadLetter) await DeadLetterAsync(incoming.Delivery.Id.Value);
        return (endpoint, incoming);
    }

    private async Task DeadLetterAsync(Guid deliveryId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<DeliveryLeaseRepository>();
        var lease = (await repository.ClaimAsync(100, TimeSpan.FromMinutes(1), default)).Single(x => x.DeliveryId == deliveryId);
        await repository.FinalizeAsync(lease, DateTimeOffset.UtcNow, 400, "http_permanent", new RetryDecision.DeadLetter("http_permanent"), default, "safe snippet");
    }
}
