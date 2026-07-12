using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayForge.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace RelayForge.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private readonly string _keysPath = Path.Combine(Path.GetTempPath(), "relayforge-tests", Guid.NewGuid().ToString("N"));
    public RelayForgeApiFactory Factory { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Factory = new RelayForgeApiFactory(_container.GetConnectionString(), _keysPath);
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>().Database.MigrateAsync();
    }

    public async Task ResetAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>().IncomingEvents.ExecuteDeleteAsync();
        await scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>().WebhookEndpoints.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _container.DisposeAsync();
        if (Directory.Exists(_keysPath)) Directory.Delete(_keysPath, recursive: true);
    }
}
