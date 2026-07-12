using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography.X509Certificates;
using RelayForge.Api.Endpoints;
using RelayForge.Infrastructure.Persistence;
using RelayForge.Infrastructure.Delivery;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddDbContextFactory<RelayForgeDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("RelayForge") ??
        "Host=localhost;Port=5432;Database=relayforge;Username=relayforge;Password=relayforge");
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DeliveryLeaseRepository>();
builder.Services.AddScoped<DeliveryDispatcher>();
builder.Services.Configure<DeliveryWorkerOptions>(builder.Configuration.GetSection("DeliveryWorker"));
builder.Services.AddHttpClient("RelayForgeDelivery").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<DeliveryWorker>();

var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (builder.Environment.IsDevelopment())
{
    keysPath ??= Path.Combine(builder.Environment.ContentRootPath, ".data-protection-keys");
}
else
{
    var certificatePath = builder.Configuration["DataProtection:CertificatePath"];
    var privateKeyPath = builder.Configuration["DataProtection:PrivateKeyPath"];
    if (string.IsNullOrWhiteSpace(keysPath) || string.IsNullOrWhiteSpace(certificatePath) || string.IsNullOrWhiteSpace(privateKeyPath))
    {
        throw new InvalidOperationException(
            "Non-development environments require DataProtection:KeysPath, DataProtection:CertificatePath and DataProtection:PrivateKeyPath. Mount a persistent shared key directory and an X509 PEM certificate with its private key.");
    }

    if (!File.Exists(certificatePath) || !File.Exists(privateKeyPath))
    {
        throw new InvalidOperationException("The configured Data Protection X509 certificate or private key file does not exist.");
    }
}

Directory.CreateDirectory(keysPath);
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("RelayForge");
if (!builder.Environment.IsDevelopment())
{
    var certificate = X509Certificate2.CreateFromPemFile(
        builder.Configuration["DataProtection:CertificatePath"]!,
        builder.Configuration["DataProtection:PrivateKeyPath"]!);
    if (!certificate.HasPrivateKey) throw new InvalidOperationException("The Data Protection X509 certificate must include a private key.");
    builder.Services.AddSingleton(certificate);
    dataProtection.ProtectKeysWithCertificate(certificate);
}

var app = builder.Build();

app.UseExceptionHandler();
app.MapHealthChecks("/health/live");
app.MapWebhookEndpoints();
app.MapEventRoutes();

app.Run();

public partial class Program;
