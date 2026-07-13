using Microsoft.EntityFrameworkCore;
using RelayForge.Infrastructure.Persistence;
using RelayForge.MigrationService;

namespace RelayForge.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class MigrationRunnerTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task ApplyAsync_is_idempotent()
    {
        var options = new DbContextOptionsBuilder<RelayForgeDbContext>()
            .UseNpgsql(postgres.ConnectionString)
            .Options;

        await using var db = new RelayForgeDbContext(options);
        await MigrationRunner.ApplyAsync(db, CancellationToken.None);
        var first = await db.Database.GetAppliedMigrationsAsync();
        await MigrationRunner.ApplyAsync(db, CancellationToken.None);
        var second = await db.Database.GetAppliedMigrationsAsync();

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }
}
