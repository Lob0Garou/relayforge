using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace RelayForge.IntegrationTests;

public sealed class RelayForgeApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _connectionString;
    private readonly string _environment;
    private readonly bool _ownsKeysPath;

    public string KeysPath { get; }

    public RelayForgeApiFactory() : this(null, Path.Combine(Path.GetTempPath(), "relayforge-tests", Guid.NewGuid().ToString("N"))) => _ownsKeysPath = true;

    internal RelayForgeApiFactory(string? connectionString, string keysPath, string environment = "Development")
    {
        _connectionString = connectionString;
        KeysPath = keysPath;
        _environment = environment;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        if (_connectionString is not null) builder.UseSetting("ConnectionStrings:RelayForge", _connectionString);
        builder.UseSetting("DataProtection:KeysPath", KeysPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_ownsKeysPath && Directory.Exists(KeysPath)) Directory.Delete(KeysPath, recursive: true);
    }
}
