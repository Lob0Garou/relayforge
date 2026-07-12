using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using RelayForge.Infrastructure.Security;
using RelayForge.UnstableReceiver;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReceiverSimulator>();
builder.Services.AddOptions<ReceiverOptions>()
    .Bind(builder.Configuration.GetSection(ReceiverOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.SigningSecret), "Receiver signing secret is required.")
    .Validate(options => options.TimestampToleranceSeconds is > 0 and <= 3_600, "Timestamp tolerance must be from 1 to 3600 seconds.")
    .Validate(options => options.MaxBodyBytes is > 0 and <= 1_048_576, "Maximum body size must be from 1 to 1048576 bytes.")
    .ValidateOnStart();

var app = builder.Build();

app.MapPut("/operations/scenario", (ReceiverScenario scenario, ReceiverSimulator simulator) =>
{
    if (scenario.FailuresBeforeSuccess is < 0 or > 100 ||
        scenario.FailureStatusCode is < 400 or > 599 ||
        scenario.DelayMilliseconds is < 0 or > 30_000)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["scenario"] = ["failuresBeforeSuccess: 0-100; failureStatusCode: 400-599; delayMilliseconds: 0-30000."]
        });
    }

    simulator.Configure(scenario);
    return Results.Ok(scenario);
});

app.MapGet("/operations/deliveries/{deliveryId}", (string deliveryId, ReceiverSimulator simulator) =>
    simulator.GetState(deliveryId) is { } state ? Results.Ok(state) : Results.NotFound());

app.MapPost("/webhooks/relayforge", async (
    HttpRequest request,
    ReceiverSimulator simulator,
    IOptions<ReceiverOptions> configuredOptions,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var options = configuredOptions.Value;
    if (!TryGetSingleHeader(request, "RelayForge-Delivery-Id", out var deliveryId) ||
        !TryGetSingleHeader(request, "RelayForge-Timestamp", out var timestampText) ||
        !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) ||
        !TryGetSingleHeader(request, "RelayForge-Signature", out var signature))
    {
        return Results.Unauthorized();
    }

    var payload = await ReadBodyAsync(request, options.MaxBodyBytes, cancellationToken);
    if (payload is null)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    var valid = WebhookSigner.Verify(
        Encoding.UTF8.GetBytes(options.SigningSecret),
        timestamp,
        deliveryId,
        payload,
        signature,
        TimeSpan.FromSeconds(options.TimestampToleranceSeconds),
        timeProvider);
    if (!valid)
    {
        return Results.Unauthorized();
    }

    var state = simulator.RecordAttempt(deliveryId);
    if (state.DelayMilliseconds > 0)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(state.DelayMilliseconds), timeProvider, cancellationToken);
    }

    return state.Attempts <= state.FailuresBeforeSuccess
        ? Results.StatusCode(state.FailureStatusCode)
        : Results.Ok(new { state.DeliveryId, state.Attempts });
});

app.Run();

static bool TryGetSingleHeader(HttpRequest request, string name, out string value)
{
    var values = request.Headers[name];
    if (values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]))
    {
        value = values[0]!;
        return true;
    }

    value = string.Empty;
    return false;
}

static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maximumBytes, CancellationToken cancellationToken)
{
    if (request.ContentLength > maximumBytes)
    {
        return null;
    }

    using var buffer = new MemoryStream(Math.Min(maximumBytes, 65_536));
    var chunk = new byte[8_192];
    while (true)
    {
        var read = await request.Body.ReadAsync(chunk, cancellationToken);
        if (read == 0)
        {
            return buffer.ToArray();
        }

        if (buffer.Length + read > maximumBytes)
        {
            return null;
        }

        await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
    }
}

namespace RelayForge.UnstableReceiver
{
    public partial class Program;
}
