using Microsoft.EntityFrameworkCore;
using RelayForge.Domain.Endpoints;
using RelayForge.Domain.Events;

namespace RelayForge.Infrastructure.Persistence;

public sealed class RelayForgeDbContext(DbContextOptions<RelayForgeDbContext> options) : DbContext(options)
{
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<IncomingEvent> IncomingEvents => Set<IncomingEvent>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyConfigurationsFromAssembly(typeof(RelayForgeDbContext).Assembly);
}
