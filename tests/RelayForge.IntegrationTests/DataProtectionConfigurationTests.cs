namespace RelayForge.IntegrationTests;

public sealed class DataProtectionConfigurationTests
{
    [Fact]
    public void Production_requires_explicit_protected_keyring_configuration()
    {
        var keysPath = Path.Combine(Path.GetTempPath(), "relayforge-tests", Guid.NewGuid().ToString("N"));
        using var factory = new RelayForgeApiFactory(null, keysPath, "Production");

        var exception = Assert.Throws<InvalidOperationException>(() => _ = factory.Services);

        Assert.Contains("DataProtection:CertificatePath", exception.ToString(), StringComparison.Ordinal);
    }
}
