using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RelayForge.Api.Endpoints;
using RelayForge.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddDbContext<RelayForgeDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("RelayForge") ??
        "Host=localhost;Port=5432;Database=relayforge;Username=relayforge;Password=relayforge");
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});

var keysPath = builder.Configuration["DataProtection:KeysPath"] ?? Path.Combine(builder.Environment.ContentRootPath, ".data-protection-keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("RelayForge");

var app = builder.Build();

app.UseExceptionHandler();
app.MapHealthChecks("/health/live");
app.MapWebhookEndpoints();

app.Run();

public partial class Program;
