using Microsoft.EntityFrameworkCore;
using RelayForge.Domain.Endpoints;
using RelayForge.Domain.Events;

namespace RelayForge.Infrastructure.Persistence;

public sealed class RelayForgeDbContext(DbContextOptions<RelayForgeDbContext> options) : DbContext(options)
{
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<IncomingEvent> IncomingEvents => Set<IncomingEvent>();
    public DbSet<RelayForge.Domain.Events.Delivery> Deliveries => Set<RelayForge.Domain.Events.Delivery>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyConfigurationsFromAssembly(typeof(RelayForgeDbContext).Assembly);
}
