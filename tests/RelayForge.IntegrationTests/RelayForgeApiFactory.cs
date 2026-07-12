using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace RelayForge.IntegrationTests;

public sealed class RelayForgeApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _connectionString;

    public RelayForgeApiFactory() { }

    internal RelayForgeApiFactory(string connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_connectionString is null) return;
        builder.UseSetting("ConnectionStrings:RelayForge", _connectionString);
        builder.UseSetting("DataProtection:KeysPath", Path.Combine(Path.GetTempPath(), "relayforge-tests", Guid.NewGuid().ToString("N")));
    }
}
