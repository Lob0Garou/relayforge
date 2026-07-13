using Microsoft.EntityFrameworkCore;
using RelayForge.Infrastructure.Persistence;
using RelayForge.MigrationService;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__RelayForge");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings__RelayForge is required.");
    return 2;
}

try
{
    var options = new DbContextOptionsBuilder<RelayForgeDbContext>().UseNpgsql(connectionString).Options;
    await using var db = new RelayForgeDbContext(options);
    await MigrationRunner.ApplyAsync(db, CancellationToken.None);
    Console.WriteLine("RelayForge database migrations are current.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"RelayForge database migration failed: {exception.Message}");
    return 1;
}
