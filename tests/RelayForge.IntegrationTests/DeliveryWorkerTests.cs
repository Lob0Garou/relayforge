using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayForge.Domain.Endpoints;
using RelayForge.Domain.Events;
using RelayForge.Infrastructure.Delivery;
using RelayForge.Infrastructure.Persistence;
using RelayForge.Infrastructure.Security;

namespace RelayForge.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class DeliveryWorkerTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Concurrent_claimers_receive_one_delivery_once()
    {
        await fixture.ResetAsync(); await SeedAsync();
        var repository = Repository();
        var claims = await Task.WhenAll(repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default), repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        Assert.Equal(1, claims.Sum(x => x.Count));
    }

    [Fact]
    public async Task Expired_lease_is_recovered_and_stale_worker_cannot_finalize()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        var stale = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await using (var db = await Factory().CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET lease_expires_at = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE \"Id\" = {stale.DeliveryId}");
        var renewed = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));

        Assert.NotEqual(stale.LeaseId, renewed.LeaseId);
        Assert.False(await repository.FinalizeAsync(stale, DateTimeOffset.UtcNow, 200, null, default));
        Assert.True(await repository.FinalizeAsync(renewed, DateTimeOffset.UtcNow, 200, null, default));
    }

    [Fact]
    public async Task Dispatcher_sends_exact_body_canonical_headers_and_hmac_then_marks_delivered()
    {
        await fixture.ResetAsync(); var secret = await SeedAsync(); var repository = Repository();
        var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var dispatcher = new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);

        await dispatcher.DispatchAsync(lease, default);

        Assert.Equal(lease.Payload, handler.Body);
        var timestamp = long.Parse(handler.Headers["X-RelayForge-Timestamp"]);
        Assert.Equal(lease.DeliveryId.ToString("D"), handler.Headers["X-RelayForge-Delivery"]);
        Assert.True(WebhookSigner.Verify(Encoding.UTF8.GetBytes(secret), timestamp, lease.DeliveryId, Encoding.UTF8.GetBytes(handler.Body), handler.Headers["X-RelayForge-Signature"], TimeSpan.FromMinutes(1), TimeProvider.System));
        await using var db = await Factory().CreateDbContextAsync();
        Assert.Equal(DeliveryStatus.Delivered, (await db.Deliveries.SingleAsync()).Status);
        Assert.Single(await db.DeliveryAttempts.ToListAsync());
    }

    [Fact]
    public async Task Http_failure_is_recorded_once_and_is_not_polled_again()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        await new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease, default);

        Assert.Empty(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await using var db = await Factory().CreateDbContextAsync();
        Assert.Equal(DeliveryStatus.RetryScheduled, (await db.Deliveries.SingleAsync()).Status);
        Assert.Single(await db.DeliveryAttempts.ToListAsync()); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Lease_transaction_is_committed_before_http_is_sent()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        var handler = new BlockingHandler();
        var dispatch = new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease, default);
        await handler.Entered.Task;
        await using var db = await Factory().CreateDbContextAsync();
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET next_attempt_at = {DateTimeOffset.UtcNow} WHERE \"Id\" = {lease.DeliveryId}"));
        handler.Release.SetResult(); await dispatch;
    }

    private IDbContextFactory<RelayForgeDbContext> Factory() => fixture.Factory.Services.GetRequiredService<IDbContextFactory<RelayForgeDbContext>>();
    private DeliveryLeaseRepository Repository() => new(Factory(), TimeProvider.System);
    private async Task<string> SeedAsync()
    {
        const string secret = "test-signing-secret";
        var protector = fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RelayForge.WebhookEndpointSecrets.v1");
        var endpointResult = WebhookEndpoint.Create("Receiver", "http://receiver.invalid/hook", TimeSpan.FromSeconds(30), protector.Protect(secret)); Assert.True(endpointResult.TryGetValue(out var endpoint));
        var eventResult = IncomingEvent.Create(endpoint.Id, "order.created", "{\"amount\":1e1}", Guid.NewGuid().ToString("N"), new string('a', 64)); Assert.True(eventResult.TryGetValue(out var incomingEvent));
        await using var db = await Factory().CreateDbContextAsync(); db.WebhookEndpoints.Add(endpoint); db.IncomingEvents.Add(incomingEvent); await db.SaveChangesAsync(); return secret;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; } public string Body { get; private set; } = ""; public Dictionary<string, string> Headers { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; Body = await request.Content!.ReadAsStringAsync(cancellationToken); foreach (var h in request.Headers) Headers[h.Key] = Assert.Single(h.Value); return new(status); }
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Entered.SetResult(); await Release.Task.WaitAsync(cancellationToken); return new(HttpStatusCode.NoContent); }
    }
}
