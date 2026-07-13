using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Numerics;
using System.Globalization;
using RelayForge.Domain.Endpoints;

namespace RelayForge.Domain.Events;

public readonly record struct IncomingEventId(Guid Value) { public static IncomingEventId New() => new(Guid.NewGuid()); }
public readonly record struct DeliveryId(Guid Value) { public static DeliveryId New() => new(Guid.NewGuid()); }
public enum EventStatus { Pending }
public enum DeliveryStatus { Pending, Processing, Delivered, RetryScheduled, DeadLettered, Replayed }
public enum DeliveryAttemptOutcome { Delivered, Failed }

public sealed class IncomingEvent
{
    public const int MaxEventTypeLength = 120;
    private IncomingEvent() { }
    private IncomingEvent(IncomingEventId id, WebhookEndpointId endpointId, string eventType, string payload, string key, string fingerprint)
    {
        Id = id; EndpointId = endpointId; EventType = eventType; Payload = payload; IdempotencyKey = key;
        Fingerprint = fingerprint; Status = EventStatus.Pending; CreatedAt = DateTimeOffset.UtcNow;
        Delivery = Delivery.Create(id, endpointId, CreatedAt);
    }
    public IncomingEventId Id { get; private set; }
    public WebhookEndpointId EndpointId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string Fingerprint { get; private set; } = string.Empty;
    public EventStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Delivery Delivery { get; private set; } = null!;

    public static DomainResult<IncomingEvent> Create(WebhookEndpointId endpointId, string eventType, string payload, string key, string fingerprint)
    {
        if (endpointId.Value == Guid.Empty) return DomainResult<IncomingEvent>.Failure(new("event.endpoint_invalid", "Endpoint is required."));
        if (string.IsNullOrWhiteSpace(eventType) || eventType.Length > MaxEventTypeLength) return DomainResult<IncomingEvent>.Failure(new("event.type_invalid", $"Event type is required and must not exceed {MaxEventTypeLength} characters."));
        if (string.IsNullOrEmpty(payload)) return DomainResult<IncomingEvent>.Failure(new("event.payload_invalid", "Payload is required."));
        if (string.IsNullOrWhiteSpace(key)) return DomainResult<IncomingEvent>.Failure(new("event.idempotency_key_invalid", "Idempotency key is required."));
        if (fingerprint.Length != 64) return DomainResult<IncomingEvent>.Failure(new("event.fingerprint_invalid", "Fingerprint is invalid."));
        return DomainResult<IncomingEvent>.Success(new(IncomingEventId.New(), endpointId, eventType.Trim(), payload, key, fingerprint));
    }
}

public sealed class Delivery
{
    private Delivery() { }
    private Delivery(DeliveryId id, IncomingEventId eventId, WebhookEndpointId endpointId, DateTimeOffset createdAt)
    { Id = id; EventId = eventId; EndpointId = endpointId; Status = DeliveryStatus.Pending; CreatedAt = createdAt; }
    public DeliveryId Id { get; private set; }
    public IncomingEventId EventId { get; private set; }
    public WebhookEndpointId EndpointId { get; private set; }
    public DeliveryStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public uint Version { get; private set; }
    public IReadOnlyCollection<DeliveryAttempt> Attempts => _attempts;
    private readonly List<DeliveryAttempt> _attempts = [];
    internal static Delivery Create(IncomingEventId eventId, WebhookEndpointId endpointId, DateTimeOffset createdAt) => new(DeliveryId.New(), eventId, endpointId, createdAt);

    public DomainResult<Delivery> StartProcessing(Guid leaseId, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (Status is not (DeliveryStatus.Pending or DeliveryStatus.Replayed) || leaseId == Guid.Empty || expiresAt <= now) return InvalidTransition();
        SetLease(leaseId, expiresAt); return DomainResult<Delivery>.Success(this);
    }

    public DomainResult<Delivery> RecoverExpiredLease(Guid leaseId, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Processing || LeaseExpiresAt is null || LeaseExpiresAt > now || leaseId == Guid.Empty || expiresAt <= now) return InvalidTransition();
        SetLease(leaseId, expiresAt); return DomainResult<Delivery>.Success(this);
    }

    public DomainResult<Delivery> MarkDelivered(Guid leaseId, DateTimeOffset now) => Finish(leaseId, DeliveryStatus.Delivered, now);
    public DomainResult<Delivery> ScheduleRetry(Guid leaseId, DateTimeOffset now) => Finish(leaseId, DeliveryStatus.RetryScheduled, now);

    public DomainResult<Delivery> DeadLetter(Guid leaseId, DateTimeOffset now) => Finish(leaseId, DeliveryStatus.DeadLettered, now);

    public DomainResult<Delivery> Replay(DateTimeOffset now)
    {
        if (Status != DeliveryStatus.DeadLettered) return InvalidTransition();
        Status = DeliveryStatus.Replayed; NextAttemptAt = now; Version++; return DomainResult<Delivery>.Success(this);
    }

    private void SetLease(Guid leaseId, DateTimeOffset expiresAt)
    { Status = DeliveryStatus.Processing; LeaseId = leaseId; LeaseExpiresAt = expiresAt; AttemptCount++; Version++; }

    private DomainResult<Delivery> Finish(Guid leaseId, DeliveryStatus target, DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Processing) return InvalidTransition();
        if (LeaseId != leaseId) return DomainResult<Delivery>.Failure(new("delivery.lease_mismatch", "The delivery lease is no longer owned by this worker."));
        Status = target; LeaseId = null; LeaseExpiresAt = null; NextAttemptAt = target == DeliveryStatus.RetryScheduled ? now : null; Version++;
        return DomainResult<Delivery>.Success(this);
    }

    private static DomainResult<Delivery> InvalidTransition() => DomainResult<Delivery>.Failure(new("delivery.invalid_transition", "The requested delivery state transition is not allowed."));
}

public sealed class DeliveryAttempt
{
    private DeliveryAttempt() { }
    private DeliveryAttempt(Guid id, DeliveryId deliveryId, int number, DateTimeOffset startedAt, DateTimeOffset completedAt, DeliveryAttemptOutcome outcome, int? statusCode, string? error, string? responseSnippet)
    { Id = id; DeliveryId = deliveryId; Number = number; StartedAt = startedAt; CompletedAt = completedAt; DurationMilliseconds = Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds); Outcome = outcome; HttpStatusCode = statusCode; Error = Sanitize(error); ResponseSnippet = Sanitize(responseSnippet, 65_547); }
    public Guid Id { get; private set; }
    public DeliveryId DeliveryId { get; private set; }
    public int Number { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset CompletedAt { get; private set; }
    public long DurationMilliseconds { get; private set; }
    public DeliveryAttemptOutcome Outcome { get; private set; }
    public int? HttpStatusCode { get; private set; }
    public string? Error { get; private set; }
    public string? ResponseSnippet { get; private set; }
    public static DeliveryAttempt Create(DeliveryId deliveryId, int number, DateTimeOffset startedAt, DateTimeOffset completedAt, DeliveryAttemptOutcome outcome, int? statusCode, string? error, string? responseSnippet = null) => new(Guid.NewGuid(), deliveryId, number, startedAt, completedAt, outcome, statusCode, error, responseSnippet);
    private static string? Sanitize(string? value, int limit = 1000) => string.IsNullOrWhiteSpace(value) ? null : new string(value.Where(c => !char.IsControl(c)).Take(limit).ToArray());
}

public static class EventFingerprint
{
    public static DomainResult<string> Create(WebhookEndpointId endpointId, string eventType, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
            var prefix = Encoding.UTF8.GetBytes($"{endpointId.Value:D}\n{eventType}\n");
            var combined = new byte[prefix.Length + stream.Length];
            prefix.CopyTo(combined, 0); stream.ToArray().CopyTo(combined, prefix.Length);
            return DomainResult<string>.Success(Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant());
        }
        catch (JsonException) { return DomainResult<string>.Failure(new("event.payload_invalid", "Payload must be valid JSON without duplicate properties.")); }
    }

    public static string Canonicalize(string payload)
    { using var document = JsonDocument.Parse(payload); using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement); return Encoding.UTF8.GetString(stream.ToArray()); }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject(); var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { if (!names.Add(property.Name)) throw new JsonException(); writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array: writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.Number: writer.WriteRawValue(NormalizeNumber(element.GetRawText()), skipInputValidation: true); break;
            default: element.WriteTo(writer); break;
        }
    }

    private static string NormalizeNumber(string raw)
    {
        var negative = raw[0] == '-'; var unsigned = negative ? raw[1..] : raw;
        var exponentIndex = unsigned.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex >= 0 ? unsigned[..exponentIndex] : unsigned;
        var explicitExponent = exponentIndex >= 0 ? BigInteger.Parse(unsigned[(exponentIndex + 1)..], CultureInfo.InvariantCulture) : BigInteger.Zero;
        var decimalIndex = mantissa.IndexOf('.');
        var fractionLength = decimalIndex >= 0 ? mantissa.Length - decimalIndex - 1 : 0;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        digits = digits.TrimEnd('0');
        var scientificExponent = explicitExponent - fractionLength + trailingZeros + digits.Length - 1;
        var coefficient = digits.Length == 1 ? digits : $"{digits[0]}.{digits[1..]}";
        return $"{(negative ? "-" : "")}{coefficient}e{scientificExponent.ToString(CultureInfo.InvariantCulture)}";
    }
}
