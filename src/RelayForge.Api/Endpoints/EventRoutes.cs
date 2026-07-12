using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RelayForge.Domain.Endpoints;
using RelayForge.Domain.Events;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.Api.Endpoints;

public static class EventRoutes
{
    private const int MaxBodyBytes = 262_144;
    public static IEndpointRouteBuilder MapEventRoutes(this IEndpointRouteBuilder routes)
    { routes.MapPost("/api/events", IngestAsync); return routes; }

    private static async Task<IResult> IngestAsync(HttpRequest request, RelayForgeDbContext db, IServiceScopeFactory scopeFactory, CancellationToken cancellationToken)
    {
        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false }) bodySizeFeature.MaxRequestBodySize = MaxBodyBytes;
        var key = request.Headers["Idempotency-Key"].ToString();
        if (key.Length is < 1 or > 200 || key.Any(char.IsControl)) return Validation("Idempotency-Key", "Idempotency-Key is required, must not exceed 200 characters, and cannot contain control characters.");
        if (request.ContentLength is > MaxBodyBytes) return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Request body too large");
        EventRequest body;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicates(root)) return Validation("body", "Body must be valid JSON without duplicate properties.");
            body = new(root.TryGetProperty("endpointId", out var id) && id.TryGetGuid(out var guid) ? guid : Guid.Empty,
                root.TryGetProperty("eventType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() ?? "" : "",
                root.TryGetProperty("payload", out var payload) ? payload.GetRawText() : "");
        }
        catch (JsonException) { return Validation("body", "Body must be valid JSON without duplicate properties."); }
        if (body.EndpointId == Guid.Empty) return Validation("endpointId", "A valid endpointId is required.");
        if (string.IsNullOrWhiteSpace(body.EventType) || body.EventType.Length > IncomingEvent.MaxEventTypeLength) return Validation("eventType", $"eventType is required and must not exceed {IncomingEvent.MaxEventTypeLength} characters.");
        if (Encoding.UTF8.GetByteCount(body.Payload) > MaxBodyBytes) return TypedResults.Problem(statusCode: 413, title: "Payload too large");
        var fingerprintResult = EventFingerprint.Create(new(body.EndpointId), body.EventType.Trim(), body.Payload);
        if (!fingerprintResult.TryGetValue(out var fingerprint)) return Validation("payload", fingerprintResult.Error.Description);
        var canonicalPayload = EventFingerprint.Canonicalize(body.Payload);
        var existing = await db.IncomingEvents.AsNoTracking().Include(x => x.Delivery).SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
        if (existing is not null) return Existing(existing, fingerprint);
        var endpoint = await db.WebhookEndpoints.AsNoTracking().Where(x => x.Id == new WebhookEndpointId(body.EndpointId)).Select(x => new { x.IsActive }).SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null) return TypedResults.Problem(statusCode: 404, title: "Endpoint not found");
        if (!endpoint.IsActive) return TypedResults.Problem(statusCode: 409, title: "Endpoint is inactive");
        var creation = IncomingEvent.Create(new(body.EndpointId), body.EventType, canonicalPayload, key, fingerprint);
        if (!creation.TryGetValue(out var incomingEvent)) return Validation(creation.Error.Code, creation.Error.Description);
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () => { await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken); db.IncomingEvents.Add(incomingEvent); await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); });
            return Accepted(incomingEvent);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_incoming_events_idempotency_key" })
        {
            db.ChangeTracker.Clear();
            await using var scope = scopeFactory.CreateAsyncScope();
            var fresh = scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>();
            var winner = await fresh.IncomingEvents.AsNoTracking().Include(x => x.Delivery).SingleAsync(x => x.IdempotencyKey == key, cancellationToken);
            return Existing(winner, fingerprint);
        }
    }
    private static bool HasDuplicates(JsonElement element) { if (element.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in element.EnumerateObject()) { if (!names.Add(p.Name) || HasDuplicates(p.Value)) return true; } } else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) if (HasDuplicates(item)) return true; return false; }
    private static IResult Existing(IncomingEvent value, string fingerprint) => value.Fingerprint == fingerprint ? Accepted(value) : TypedResults.Problem(statusCode: 409, title: "Idempotency conflict", detail: "The global idempotency key was already used with different content.");
    private static IResult Accepted(IncomingEvent value) => TypedResults.Accepted($"/api/events/{value.Id.Value}", new EventResponse(value.Id.Value, value.Delivery.Id.Value, "Pending"));
    private static IResult Validation(string key, string message) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });
    private sealed record EventRequest(Guid EndpointId, string EventType, string Payload);
    private sealed record EventResponse(Guid EventId, Guid DeliveryId, string State);
}
