using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography.X509Certificates;
using RelayForge.Api.Endpoints;
using RelayForge.Infrastructure.Persistence;
using RelayForge.Infrastructure.Delivery;
using RelayForge.Domain.Retry;

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
builder.Services.AddSingleton<IDestinationResolver, SystemDestinationResolver>();
builder.Services.AddScoped<DeliveryLeaseRepository>();
builder.Services.AddScoped<DeliveryDispatcher>();
builder.Services.AddSingleton<IJitterSource, SystemJitterSource>();
builder.Services.AddSingleton(sp => new RetryPolicy(new RetryPolicyOptions(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeliveryWorkerOptions>>().Value.MaxAttempts), sp.GetRequiredService<IJitterSource>()));
builder.Services.AddOptions<DeliveryWorkerOptions>().Bind(builder.Configuration.GetSection("DeliveryWorker"))
    .Validate(x => x.BatchSize is >= 1 and <= 100, "BatchSize must be between 1 and 100.")
    .Validate(x => x.MaxConcurrency is >= 1 and <= 64, "MaxConcurrency must be between 1 and 64.")
    .Validate(x => x.MaxAttempts is >= 1 and <= 100, "MaxAttempts must be between 1 and 100.")
    .Validate(x => x.PollInterval >= TimeSpan.FromMilliseconds(100) && x.PollInterval <= TimeSpan.FromMinutes(5), "PollInterval is out of range.")
    .Validate(x => x.LeaseDuration >= TimeSpan.FromSeconds(5) && x.LeaseDuration <= TimeSpan.FromMinutes(30), "LeaseDuration is out of range.")
    .ValidateOnStart();
builder.Services.AddOptions<OutboundDeliveryOptions>().Bind(builder.Configuration.GetSection("OutboundDelivery"))
    .Validate(options =>
    {
        try { OutboundDeliveryOptions.Validate(options, builder.Environment.IsDevelopment()); return true; }
        catch (InvalidOperationException) { return false; }
    }, "Outbound delivery configuration is invalid or unsafe for this environment.")
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboundDeliveryOptions>>().Value);
builder.Services.AddHttpClient("RelayForgeDelivery").ConfigurePrimaryHttpMessageHandler(sp =>
{
    var options = sp.GetRequiredService<OutboundDeliveryOptions>();
    var connector = new DestinationConnector(new DestinationPolicy(options.AllowedPrivateHosts), sp.GetRequiredService<IDestinationResolver>());
    return new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = options.PooledConnectionLifetime,
        ConnectCallback = connector.ConnectAsync
    };
});
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
app.MapDeadLetterRoutes();

app.Run();

public partial class Program;
