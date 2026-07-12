using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
    public async Task Two_concurrent_dispatchers_make_exactly_one_http_call()
    {
        await fixture.ResetAsync(); await SeedAsync(); var handler = new BlockingHandler();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var started = 0;
        Task RunIndependentDispatcherAsync()
        {
            var repository = new DeliveryLeaseRepository(Factory(), TimeProvider.System);
            var dispatcher = new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
            return RunAsync();
            async Task RunAsync()
            {
                if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult();
                await start.Task;
                foreach (var lease in await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default)) await dispatcher.DispatchAsync(lease, default);
            }
        }
        var first = RunIndependentDispatcherAsync(); var second = RunIndependentDispatcherAsync();
        await bothStarted.Task; start.SetResult(); await handler.Entered.Task;
        Assert.Equal(1, handler.Calls); handler.Release.SetResult(); await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task Replayed_delivery_is_claimed_but_retry_scheduled_is_not()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        await using (var db = await Factory().CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("UPDATE deliveries SET \"Status\" = 'Replayed'");
        Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await using (var db = await Factory().CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("UPDATE deliveries SET \"Status\" = 'RetryScheduled', lease_id = NULL, lease_expires_at = NULL");
        Assert.Empty(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Expired_owner_cannot_finalize_without_a_new_claimant()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await using (var db = await Factory().CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET lease_expires_at = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE \"Id\" = {lease.DeliveryId}");
        Assert.False(await repository.FinalizeAsync(lease, DateTimeOffset.UtcNow, 200, null, default));
    }

    [Fact]
    public async Task Release_requires_fencing_and_returns_unstarted_work_to_pending()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        Assert.False(await repository.ReleaseAsync(lease with { LeaseId = Guid.NewGuid() }, null, "cancelled", default));
        Assert.True(await repository.ReleaseAsync(lease, null, "cancelled", default));
        await using var db = await Factory().CreateDbContextAsync(); Assert.Equal(DeliveryStatus.Pending, (await db.Deliveries.SingleAsync()).Status);
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
    public async Task Network_failure_is_recorded_once_as_retry_scheduled()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository(); var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await new DeliveryDispatcher(repository, new SingleClientFactory(new NetworkFailureHandler()), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease, default);
        await AssertFailedOnceAsync();
    }

    [Fact]
    public async Task Timeout_is_recorded_once_as_retry_scheduled()
    {
        await fixture.ResetAsync(); await SeedAsync(TimeSpan.FromSeconds(1)); var repository = Repository(); var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        var handler = new NeverCompletesHandler();
        await new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease, default);
        Assert.True(handler.Entered.Task.IsCompleted); await AssertFailedOnceAsync();
    }

    [Fact]
    public async Task Graceful_cancellation_releases_started_attempt_and_preserves_cancellation()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository(); var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        var handler = new BlockingHandler(); using var cancellation = new CancellationTokenSource();
        var dispatch = new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease, cancellation.Token);
        await handler.Entered.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        await AssertFailedOnceAsync();
    }

    [Fact]
    public async Task Disabled_worker_does_not_claim_pending_delivery()
    {
        await fixture.ResetAsync(); await SeedAsync();
        var worker = new DeliveryWorker(fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new DeliveryWorkerOptions { Enabled = false }));
        await worker.StartAsync(default); await worker.StopAsync(default);
        await using var db = await Factory().CreateDbContextAsync(); Assert.Equal(DeliveryStatus.Pending, (await db.Deliveries.SingleAsync()).Status);
    }

    [Fact]
    public async Task Enabled_worker_gracefully_cancels_http_releases_lease_and_can_reclaim_after_replay()
    {
        await fixture.ResetAsync(); await SeedAsync(); var handler = new BlockingHandler();
        await using var services = new ServiceCollection()
            .AddSingleton(Factory()).AddSingleton(TimeProvider.System)
            .AddSingleton<IDataProtectionProvider>(fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>())
            .AddSingleton<IHttpClientFactory>(new SingleClientFactory(handler))
            .AddScoped<DeliveryLeaseRepository>().AddScoped<DeliveryDispatcher>().BuildServiceProvider();
        var worker = new DeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new DeliveryWorkerOptions
        { Enabled = true, BatchSize = 1, MaxConcurrency = 1, PollInterval = TimeSpan.FromMinutes(1), LeaseDuration = TimeSpan.FromMinutes(1) }));
        await worker.StartAsync(default); await handler.Entered.Task;

        await worker.StopAsync(default);

        Assert.True(handler.Cancelled.Task.IsCompleted);
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var delivery = await db.Deliveries.SingleAsync(); Assert.Equal(DeliveryStatus.RetryScheduled, delivery.Status); Assert.Null(delivery.LeaseId);
            await db.Database.ExecuteSqlRawAsync("UPDATE deliveries SET \"Status\" = 'Replayed'");
        }
        Assert.Single(await Repository().ClaimAsync(1, TimeSpan.FromMinutes(1), default));
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
    private async Task AssertFailedOnceAsync()
    {
        await using var db = await Factory().CreateDbContextAsync(); Assert.Equal(DeliveryStatus.RetryScheduled, (await db.Deliveries.SingleAsync()).Status); Assert.Single(await db.DeliveryAttempts.ToListAsync());
    }
    private async Task<string> SeedAsync(TimeSpan? timeout = null)
    {
        const string secret = "test-signing-secret";
        var protector = fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RelayForge.WebhookEndpointSecrets.v1");
        var endpointResult = WebhookEndpoint.Create("Receiver", "http://receiver.invalid/hook", timeout ?? TimeSpan.FromSeconds(30), protector.Protect(secret)); Assert.True(endpointResult.TryGetValue(out var endpoint));
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
        public int Calls { get; private set; } public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; Entered.TrySetResult(); try { await Release.Task.WaitAsync(cancellationToken); } catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; } return new(HttpStatusCode.NoContent); }
    }
    private sealed class NetworkFailureHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable"); }
    private sealed class NeverCompletesHandler : HttpMessageHandler { private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Entered.SetResult(); await _never.Task.WaitAsync(cancellationToken); throw new InvalidOperationException(); } }
}
