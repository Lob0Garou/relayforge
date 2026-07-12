using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace RelayForge.Infrastructure.Delivery;

public sealed class DeliveryWorkerOptions { public bool Enabled { get; set; } = true; public int BatchSize { get; set; } = 10; public int MaxConcurrency { get; set; } = 4; public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1); public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2); }

public sealed class DeliveryWorker(IServiceScopeFactory scopes, IOptions<DeliveryWorkerOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value; if (!settings.Enabled) return;
        using var timer = new PeriodicTimer(settings.PollInterval);
        do
        {
            await using var scope = scopes.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<DeliveryLeaseRepository>();
            var leases = await repository.ClaimAsync(settings.BatchSize, settings.LeaseDuration, stoppingToken);
            try
            {
                await Parallel.ForEachAsync(leases, new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrency, CancellationToken = stoppingToken },
                    async (lease, token) => { await using var itemScope = scopes.CreateAsyncScope(); await itemScope.ServiceProvider.GetRequiredService<DeliveryDispatcher>().DispatchAsync(lease, token); });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                foreach (var lease in leases) await repository.ReleaseAsync(lease, null, "worker_stopped", CancellationToken.None);
                throw;
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
