using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
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
    public async Task Claim_snapshot_stays_bound_to_original_lease_while_competitor_skips_locked_row()
    {
        await fixture.ResetAsync(); await SeedAsync(); var observer = new BlockingClaimObserver();
        var firstRepository = new DeliveryLeaseRepository(Factory(), TimeProvider.System, observer);
        var firstTask = firstRepository.ClaimAsync(1, TimeSpan.FromMinutes(1), default);
        await observer.Captured.Task;
        var competitor = await new DeliveryLeaseRepository(Factory(), TimeProvider.System).ClaimAsync(1, TimeSpan.FromMinutes(1), default);
        Assert.Empty(competitor);
        observer.Release.SetResult(); var first = Assert.Single(await firstTask);
        await using var db = await Factory().CreateDbContextAsync(); var stored = await db.Deliveries.SingleAsync();
        Assert.Equal(stored.LeaseId, first.LeaseId); Assert.Equal(stored.Version, first.FencingToken);
    }

    [Fact]
    public async Task Process_clock_skew_cannot_steal_live_lease_or_finalize_database_expired_lease()
    {
        await fixture.ResetAsync(); await SeedAsync();
        var future = new DeliveryLeaseRepository(Factory(), new FixedTimeProvider(DateTimeOffset.MaxValue.AddDays(-1)));
        var past = new DeliveryLeaseRepository(Factory(), new FixedTimeProvider(DateTimeOffset.MinValue.AddDays(1)));
        var original = Assert.Single(await future.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        Assert.Empty(await past.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await using (var db = await Factory().CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("UPDATE deliveries SET lease_expires_at = clock_timestamp() - interval '1 second'");
        var recovered = Assert.Single(await past.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        Assert.NotEqual(original.LeaseId, recovered.LeaseId); Assert.False(await future.FinalizeAsync(original, DateTimeOffset.UtcNow, 200, null, default));
    }
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
    public async Task Dispatcher_persists_only_a_bounded_sanitized_response_snippet()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        var handler = new BodyHandler("visible\0" + new string('x', 32), "COOKIE-SENTINEL");
        await new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System,
            responseOptions: new OutboundDeliveryOptions { MaxResponseSnippetBytes = 8 }).DispatchAsync(lease, default);

        await using var db = await Factory().CreateDbContextAsync();
        var attempt = Assert.Single(await db.DeliveryAttempts.ToListAsync());
        Assert.Equal("visible[truncated]", attempt.ResponseSnippet);
        Assert.DoesNotContain("COOKIE-SENTINEL", attempt.ResponseSnippet);
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

    [Theory]
    [InlineData(1, DeliveryStatus.RetryScheduled)]
    [InlineData(5, DeliveryStatus.DeadLettered)]
    public async Task Cancellation_uses_retry_policy_and_fifth_attempt_cannot_be_claimed_again(int attemptNumber, DeliveryStatus expected)
    {
        await fixture.ResetAsync(); await SeedAsync();
        if (attemptNumber == 5)
        {
            await using var setup = await Factory().CreateDbContextAsync();
            await setup.Database.ExecuteSqlRawAsync("UPDATE deliveries SET \"Status\" = 'RetryScheduled', attempt_count = 4, next_attempt_at = clock_timestamp() - interval '1 second'");
        }
        var repository = Repository(); var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        var handler = new BlockingHandler(); using var cancellation = new CancellationTokenSource();
        var dispatch = new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease, cancellation.Token);
        await handler.Entered.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        await using var db = await Factory().CreateDbContextAsync(); var delivery = await db.Deliveries.SingleAsync(); var recorded = Assert.Single(await db.DeliveryAttempts.ToListAsync());
        Assert.Equal(expected, delivery.Status); Assert.Equal(attemptNumber, recorded.Number); Assert.Equal("delivery_cancelled", recorded.Error);
        if (attemptNumber == 5) Assert.Empty(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Expired_fifth_attempt_is_dead_lettered_once_and_never_claimed_as_sixth_attempt()
    {
        await fixture.ResetAsync(); await SeedAsync();
        await using (var setup = await Factory().CreateDbContextAsync())
            await setup.Database.ExecuteSqlRawAsync("UPDATE deliveries SET \"Status\"='Processing', attempt_count=5, lease_id=gen_random_uuid(), lease_expires_at=clock_timestamp()-interval '1 second', version=5");
        var first = Repository(); var second = Repository();
        var claims = await Task.WhenAll(first.ClaimAsync(1, TimeSpan.FromMinutes(1), default), second.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        Assert.All(claims, Assert.Empty);
        Assert.Empty(await first.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await using var db = await Factory().CreateDbContextAsync(); var delivery = await db.Deliveries.SingleAsync(); var attempt = Assert.Single(await db.DeliveryAttempts.ToListAsync());
        Assert.Equal(DeliveryStatus.DeadLettered, delivery.Status); Assert.Equal(5, delivery.AttemptCount);
        Assert.Equal(5, attempt.Number); Assert.Equal("lease_expired_at_attempt_limit", attempt.Error);
    }

    [Fact]
    public async Task Unexpected_policy_failure_dead_letters_sanitized_attempt_before_propagating()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository(); var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        var policy = new RelayForge.Domain.Retry.RetryPolicy(new(), new ThrowingJitter());
        var dispatcher = new DeliveryDispatcher(repository, new SingleClientFactory(new RecordingHandler(HttpStatusCode.ServiceUnavailable)), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System, policy);
        var propagated = await Assert.ThrowsAsync<FinalizedDeliveryPolicyException>(() => dispatcher.DispatchAsync(lease, default));
        Assert.IsType<InvalidOperationException>(propagated.InnerException);
        await using var db = await Factory().CreateDbContextAsync(); Assert.Equal(DeliveryStatus.DeadLettered, (await db.Deliveries.SingleAsync()).Status);
        Assert.Equal("processing_failure", Assert.Single(await db.DeliveryAttempts.ToListAsync()).Error);
    }

    [Fact]
    public async Task Worker_isolates_finalized_policy_fault_and_delivers_sibling_and_next_poll()
    {
        await fixture.ResetAsync(); await SeedAsync(); await SeedAsync();
        var handler = new SequencedResponseHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.NoContent, HttpStatusCode.NoContent);
        var policy = new RelayForge.Domain.Retry.RetryPolicy(new(), new FirstThrowJitter());
        await using var services = new ServiceCollection().AddSingleton(Factory()).AddSingleton(TimeProvider.System)
            .AddSingleton<IDataProtectionProvider>(fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>()).AddSingleton<IHttpClientFactory>(new SingleClientFactory(handler))
            .AddSingleton(policy).AddScoped<DeliveryLeaseRepository>().AddScoped<DeliveryDispatcher>().BuildServiceProvider();
        var worker = new DeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new DeliveryWorkerOptions { Enabled = true, BatchSize = 2, MaxConcurrency = 1, PollInterval = TimeSpan.FromMilliseconds(100), LeaseDuration = TimeSpan.FromMinutes(1) }));
        await worker.StartAsync(default); await WaitForAttemptsAsync(2);
        await SeedAsync(); await WaitForAttemptsAsync(3); await worker.StopAsync(default);
        await using var db = await Factory().CreateDbContextAsync(); var deliveries = await db.Deliveries.OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Single(deliveries, x => x.Status == DeliveryStatus.DeadLettered); Assert.Equal(2, deliveries.Count(x => x.Status == DeliveryStatus.Delivered));
        Assert.Equal("processing_failure", Assert.Single(await db.DeliveryAttempts.Where(x => x.Outcome == DeliveryAttemptOutcome.Failed).ToListAsync()).Error);
    }

    [Fact]
    public async Task Exhausted_sweep_is_bounded_by_batch_and_repeated_calls_drain_without_duplicates()
    {
        await fixture.ResetAsync(); await SeedAsync(); await SeedAsync(); await SeedAsync();
        await using (var setup = await Factory().CreateDbContextAsync()) await setup.Database.ExecuteSqlRawAsync("UPDATE deliveries SET \"Status\"='Processing', attempt_count=5, lease_id=gen_random_uuid(), lease_expires_at=clock_timestamp()-interval '1 second', version=5");
        var repository = Repository();
        for (var expected = 1; expected <= 3; expected++)
        {
            Assert.Empty(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
            await using var check = await Factory().CreateDbContextAsync(); Assert.Equal(expected, await check.Deliveries.CountAsync(x => x.Status == DeliveryStatus.DeadLettered)); Assert.Equal(expected, await check.DeliveryAttempts.CountAsync());
        }
        Assert.Empty(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await using var verify = await Factory().CreateDbContextAsync(); Assert.Equal(3, await verify.DeliveryAttempts.CountAsync());
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
    public async Task Poison_delivery_is_sanitized_while_healthy_delivery_succeeds_in_same_worker_batch()
    {
        await fixture.ResetAsync(); await SeedAsync(); await SeedAsync();
        await using (var db = await Factory().CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("UPDATE webhook_endpoints SET protected_secret = 'invalid' WHERE id = (SELECT id FROM webhook_endpoints ORDER BY created_at LIMIT 1)");
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        await using var services = new ServiceCollection().AddSingleton(Factory()).AddSingleton(TimeProvider.System)
            .AddSingleton<IDataProtectionProvider>(fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>()).AddSingleton<IHttpClientFactory>(new SingleClientFactory(handler))
            .AddScoped<DeliveryLeaseRepository>().AddScoped<DeliveryDispatcher>().BuildServiceProvider();
        var worker = new DeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new DeliveryWorkerOptions { Enabled = true, BatchSize = 2, MaxConcurrency = 2, PollInterval = TimeSpan.FromMinutes(1), LeaseDuration = TimeSpan.FromMinutes(1) }));
        await worker.StartAsync(default); await WaitForAttemptsAsync(2); await worker.StopAsync(default);
        await using var verify = await Factory().CreateDbContextAsync(); var attempts = await verify.DeliveryAttempts.OrderBy(x => x.Outcome).ToListAsync();
        Assert.Equal(2, attempts.Count); Assert.Contains(attempts, x => x.Outcome == DeliveryAttemptOutcome.Delivered);
        var poison = Assert.Single(attempts, x => x.Outcome == DeliveryAttemptOutcome.Failed); Assert.Equal("processing_failure", poison.Error); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Unexpected_handler_exception_is_sanitized_and_does_not_block_healthy_sibling()
    {
        await fixture.ResetAsync(); await SeedAsync(); await SeedAsync(); var repository = Repository();
        var leases = await repository.ClaimAsync(2, TimeSpan.FromMinutes(1), default); Assert.Equal(2, leases.Count);
        var handler = new FirstThrowsHandler(); var dispatcher = new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
        await Task.WhenAll(leases.Select(x => dispatcher.DispatchAsync(x, default)));
        await using var db = await Factory().CreateDbContextAsync(); var attempts = await db.DeliveryAttempts.ToListAsync();
        Assert.Contains(attempts, x => x.Outcome == DeliveryAttemptOutcome.Delivered); var failed = Assert.Single(attempts, x => x.Outcome == DeliveryAttemptOutcome.Failed); Assert.Equal("processing_failure", failed.Error);
    }

    [Fact]
    public async Task Future_retry_is_not_claimed_but_database_due_retry_is_claimed()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await new DeliveryDispatcher(repository, new SingleClientFactory(new RecordingHandler(HttpStatusCode.ServiceUnavailable)), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease, default);
        Assert.Empty(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await using (var db = await Factory().CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("UPDATE deliveries SET next_attempt_at = clock_timestamp() - interval '1 millisecond'");
        Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Permanent_400_dead_letters_immediately_and_503_dead_letters_on_fifth_attempt()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await new DeliveryDispatcher(repository, new SingleClientFactory(new RecordingHandler(HttpStatusCode.BadRequest)), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease, default);
        await using (var db = await Factory().CreateDbContextAsync()) Assert.Equal(DeliveryStatus.DeadLettered, (await db.Deliveries.SingleAsync()).Status);

        await fixture.ResetAsync(); await SeedAsync(); repository = Repository();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
            await new DeliveryDispatcher(repository, new SingleClientFactory(new RecordingHandler(HttpStatusCode.ServiceUnavailable)), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease, default);
            if (attempt < 5) await using (var db = await Factory().CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("UPDATE deliveries SET next_attempt_at = clock_timestamp() - interval '1 millisecond'");
        }
        await using var verify = await Factory().CreateDbContextAsync(); var delivery = await verify.Deliveries.SingleAsync();
        Assert.Equal(DeliveryStatus.DeadLettered, delivery.Status); Assert.Equal(5, delivery.AttemptCount); Assert.Equal(5, await verify.DeliveryAttempts.CountAsync());
    }

    [Fact]
    public async Task Transient_failures_twice_then_succeed_on_third_and_429_respects_retry_after()
    {
        await fixture.ResetAsync(); await SeedAsync(); var repository = Repository();
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.NoContent);
        var dispatcher = new DeliveryDispatcher(repository, new SingleClientFactory(handler), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var lease = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default)); await dispatcher.DispatchAsync(lease, default);
            if (attempt < 3) await using (var db = await Factory().CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("UPDATE deliveries SET next_attempt_at = clock_timestamp() - interval '1 millisecond'");
        }
        await using (var db = await Factory().CreateDbContextAsync()) { Assert.Equal(DeliveryStatus.Delivered, (await db.Deliveries.SingleAsync()).Status); Assert.Equal(3, await db.DeliveryAttempts.CountAsync()); }

        await fixture.ResetAsync(); await SeedAsync(); repository = Repository(); var lease429 = Assert.Single(await repository.ClaimAsync(1, TimeSpan.FromMinutes(1), default));
        await new DeliveryDispatcher(repository, new SingleClientFactory(new RetryAfterHandler()), fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System).DispatchAsync(lease429, default);
        await using var verify = await Factory().CreateDbContextAsync(); var seconds = await verify.Database.SqlQuery<double>($"SELECT EXTRACT(EPOCH FROM (next_attempt_at - clock_timestamp())) AS \"Value\" FROM deliveries").SingleAsync();
        Assert.InRange(seconds, 115, 120);
    }

    [Fact]
    public async Task Polling_indexes_exist_and_are_selected_for_ready_and_expired_queries()
    {
        await fixture.ResetAsync(); await SeedAsync(); await using var db = await Factory().CreateDbContextAsync();
        var definitions = await db.Database.SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'deliveries' AND indexname IN ('ix_deliveries_ready','ix_deliveries_expired_leases')").ToListAsync();
        Assert.Equal(2, definitions.Count); Assert.Contains(definitions, x => x.Contains("Pending", StringComparison.Ordinal) && x.Contains("Replayed", StringComparison.Ordinal)); Assert.Contains(definitions, x => x.Contains("lease_expires_at", StringComparison.Ordinal));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TEMP TABLE synthetic_deliveries(event_id uuid, delivery_id uuid, ordinal integer);
            INSERT INTO synthetic_deliveries SELECT gen_random_uuid(), gen_random_uuid(), n FROM generate_series(1, 500) n;
            INSERT INTO incoming_events ("Id", "EndpointId", "EventType", "Payload", "IdempotencyKey", "Fingerprint", "Status", "CreatedAt")
              SELECT event_id, (SELECT id FROM webhook_endpoints LIMIT 1), 'synthetic', '{{}}'::jsonb, 'synthetic-' || ordinal, repeat('a',64), 'Pending', clock_timestamp() - ordinal * interval '1 second' FROM synthetic_deliveries;
            INSERT INTO deliveries ("Id", "EventId", "EndpointId", "Status", "CreatedAt", attempt_count, version, lease_expires_at)
              SELECT delivery_id, event_id, (SELECT id FROM webhook_endpoints LIMIT 1), CASE WHEN ordinal % 2 = 0 THEN 'Pending' ELSE 'Processing' END,
                clock_timestamp() - ordinal * interval '1 second', 0, 0, CASE WHEN ordinal % 2 = 1 THEN clock_timestamp() - interval '1 minute' END FROM synthetic_deliveries;
            """);
        await db.Database.ExecuteSqlRawAsync("SET enable_seqscan = off");
        var readyPlan = await db.Database.SqlQuery<string>($"EXPLAIN SELECT \"Id\" FROM deliveries WHERE \"Status\" IN ('Pending','Replayed') ORDER BY \"Status\", \"CreatedAt\" LIMIT 10").ToListAsync();
        var expiredPlan = await db.Database.SqlQuery<string>($"EXPLAIN SELECT \"Id\" FROM deliveries WHERE \"Status\" = 'Processing' AND lease_expires_at <= clock_timestamp() ORDER BY lease_expires_at, \"CreatedAt\" LIMIT 10").ToListAsync();
        Assert.Contains(readyPlan, x => x.Contains("ix_deliveries_ready", StringComparison.Ordinal)); Assert.Contains(expiredPlan, x => x.Contains("ix_deliveries_expired_leases", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_worker_options_fail_validation_on_start()
    {
        var services = new ServiceCollection(); services.AddOptions<DeliveryWorkerOptions>().Configure(x => x.BatchSize = 0)
            .Validate(x => x.BatchSize is >= 1 and <= 100, "invalid").ValidateOnStart();
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
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
    private async Task WaitForAttemptsAsync(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true) { await using var db = await Factory().CreateDbContextAsync(timeout.Token); if (await db.DeliveryAttempts.CountAsync(timeout.Token) == count) return; await Task.Yield(); timeout.Token.ThrowIfCancellationRequested(); }
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
    private sealed class BlockingClaimObserver : IDeliveryClaimObserver { public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public async Task SnapshotCapturedAsync(CancellationToken cancellationToken) { Captured.SetResult(); await Release.Task.WaitAsync(cancellationToken); } }
    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }
    private sealed class FirstThrowsHandler : HttpMessageHandler { private int _calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Interlocked.Increment(ref _calls) == 1 ? throw new InvalidOperationException("secret details") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler { private int _calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(statuses[Interlocked.Increment(ref _calls) - 1])); }
    private sealed class RetryAfterHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120)); return Task.FromResult(response); } }
    private sealed class BodyHandler(string body, string cookie) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { var response = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body) }; response.Headers.Add("Set-Cookie", cookie); return Task.FromResult(response); } }
    private sealed class ThrowingJitter : RelayForge.Domain.Retry.IJitterSource { public double NextUnit() => throw new InvalidOperationException("policy defect details"); }
    private sealed class FirstThrowJitter : RelayForge.Domain.Retry.IJitterSource { private int _calls; public double NextUnit() => Interlocked.Increment(ref _calls) == 1 ? throw new InvalidOperationException("sensitive policy details") : .5; }
    private sealed class SequencedResponseHandler(params HttpStatusCode[] statuses) : HttpMessageHandler { private int _calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(statuses[Interlocked.Increment(ref _calls) - 1])); }
}
