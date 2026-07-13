using Microsoft.EntityFrameworkCore;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.MigrationService;

public static class MigrationRunner
{
    public static Task ApplyAsync(RelayForgeDbContext db, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => db.Database.MigrateAsync(cancellationToken));
    }
}
