using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace RelayForge.Infrastructure.Delivery;

public sealed class DeliveryWorkerOptions { public bool Enabled { get; set; } = true; public int BatchSize { get; set; } = 10; public int MaxConcurrency { get; set; } = 4; public int MaxAttempts { get; set; } = RelayForge.Domain.Retry.RetryPolicyOptions.DefaultMaxAttempts; public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1); public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2); }

public sealed class DeliveryWorker(IServiceScopeFactory scopes, IOptions<DeliveryWorkerOptions> options, ILogger<DeliveryWorker>? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value; if (!settings.Enabled) return;
        using var timer = new PeriodicTimer(settings.PollInterval);
        do
        {
            await using var scope = scopes.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<DeliveryLeaseRepository>();
            var leases = await repository.ClaimAsync(settings.BatchSize, settings.LeaseDuration, stoppingToken, settings.MaxAttempts);
            try
            {
                await Parallel.ForEachAsync(leases, new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrency, CancellationToken = stoppingToken },
                    async (lease, token) =>
                    {
                        await using var itemScope = scopes.CreateAsyncScope();
                        try { await itemScope.ServiceProvider.GetRequiredService<DeliveryDispatcher>().DispatchAsync(lease, token); }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (FinalizedDeliveryPolicyException finalized)
                        {
                            logger?.LogError("Delivery item failed after sanitized finalization. Code={Code} DeliveryId={DeliveryId}", "delivery_policy_failure", finalized.DeliveryId);
                        }
                    });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                foreach (var lease in leases) await repository.ReleaseUnstartedAsync(lease, "worker_stopped_before_dispatch", CancellationToken.None);
                throw;
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
