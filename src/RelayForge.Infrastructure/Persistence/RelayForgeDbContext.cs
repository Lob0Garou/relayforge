using Microsoft.EntityFrameworkCore;
using RelayForge.Domain.Endpoints;

namespace RelayForge.Infrastructure.Persistence;

public sealed class RelayForgeDbContext(DbContextOptions<RelayForgeDbContext> options) : DbContext(options)
{
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyConfigurationsFromAssembly(typeof(RelayForgeDbContext).Assembly);
}
