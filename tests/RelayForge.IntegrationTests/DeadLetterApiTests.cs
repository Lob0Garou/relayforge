using System.Net.Http.Json;
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
public sealed class DeadLetterApiTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Lists_paginated_dead_letters_without_payload_or_secret()
    {
        await fixture.ResetAsync();
        var protector = fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RelayForge.WebhookEndpointSecrets.v1");
        var endpointResult = WebhookEndpoint.Create("Receiver", "http://receiver.invalid/hook", TimeSpan.FromSeconds(30), protector.Protect("never-expose-me")); Assert.True(endpointResult.TryGetValue(out var endpoint));
        var eventResult = IncomingEvent.Create(endpoint.Id, "secret.event", "{\"private\":true}", Guid.NewGuid().ToString("N"), new string('a', 64)); Assert.True(eventResult.TryGetValue(out var incoming));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>(); db.WebhookEndpoints.Add(endpoint); db.IncomingEvents.Add(incoming); await db.SaveChangesAsync(); }
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<DeliveryLeaseRepository>(); var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
            await repository.FinalizeAsync(lease, DateTimeOffset.UtcNow, 400, "http_permanent", new RetryDecision.DeadLetter("http_permanent"), default);
        }

        var json = await (await fixture.Factory.CreateClient().GetAsync("/api/dead-letters?page=0&pageSize=9999")).Content.ReadAsStringAsync();
        Assert.Contains("\"page\":1", json); Assert.Contains("\"pageSize\":100", json); Assert.Contains("http_permanent", json);
        Assert.DoesNotContain("private", json); Assert.DoesNotContain("never-expose-me", json); Assert.DoesNotContain("protected", json, StringComparison.OrdinalIgnoreCase);
    }
}
