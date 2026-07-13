# RelayForge

## Event ingestion idempotency

`POST /api/events` uses a single-tenant, globally unique `Idempotency-Key` in the MVP. Replays with the same endpoint, event type, and semantically equivalent JSON payload return the original event and delivery IDs. Reusing the key for any different endpoint, event type, or payload returns `409 Conflict`.

JSON numbers are canonicalized exactly; equivalent spellings share a fingerprint, and negative zero is intentionally equivalent to zero.

Endpoint activity is checked under a PostgreSQL `FOR SHARE` row lock in the same transaction that creates the event and delivery. Any future endpoint-deactivation path must first acquire `FOR UPDATE` (or another incompatible row lock) in its update transaction so ingestion and deactivation have a single database-defined order.

Reliable webhook delivery and replay platform built with .NET 8 and React.

## Outbound delivery security

Outbound delivery resolves a hostname once inside `SocketsHttpHandler.ConnectCallback`, rejects the
entire DNS answer set when any address is private or otherwise non-public, and connects directly to
the selected validated IP while retaining the original hostname for the HTTP Host header and TLS SNI.
Redirects and proxy/environment-proxy use are disabled. Connections are pooled for a bounded lifetime;
each new pooled connection repeats this single-resolution validation, while reuse avoids DNS churn.

Development may explicitly allow exact private hostnames such as `unstable-receiver` or `localhost`
through `OutboundDelivery:AllowedPrivateHosts`. Wildcards and IP literals are not accepted, and any
private-host allowlist outside Development fails startup validation.

Responses use `ResponseHeadersRead` and are streamed into a bounded, control-character-sanitized
snippet. Response headers, cookies, authorization values, signatures, protected endpoint secrets,
and full request payloads are never included in that persisted snippet.
The dispatcher also removes exact payload, signature, and signing-secret values echoed by a receiver.
This value-based redaction cannot identify secrets unknown to RelayForge; the dispatcher therefore
does not send Cookie or Authorization headers, and response headers such as Set-Cookie are never persisted.

RelayForge is being developed in public through small, verifiable milestones. The first executable foundation is available.

The projects target .NET 8 (`net8.0`). The repository quickstart requires the .NET SDK 10.0.301 or newer in the same feature band, as pinned by `global.json`, to support the `.slnx` solution format.

## Quickstart

```powershell
dotnet tool restore
dotnet build RelayForge.slnx
dotnet test RelayForge.slnx
dotnet run --project src/RelayForge.Api
```

The live health endpoint is available at `/health/live`.

### Data Protection outside Development

Production-like environments must mount a persistent keyring shared by all application instances and an X509 certificate/private key pair in PEM format. Configure these values through environment variables; never commit the certificate or private key:

```powershell
$env:DataProtection__KeysPath = 'C:\relayforge\keyring'
$env:DataProtection__CertificatePath = 'C:\run\secrets\data-protection.crt.pem'
$env:DataProtection__PrivateKeyPath = 'C:\run\secrets\data-protection.key.pem'
```

The local unprotected keyring fallback is enabled only in the `Development` environment.

## Webhook signature protocol and failure simulator

RelayForge webhook signatures use HMAC-SHA256. The signed bytes are exactly the UTF-8 bytes of
`{unixTimestamp}.{deliveryId}.` followed by the raw request body bytes, with no JSON parsing or
canonicalization. Delivery IDs use canonical lowercase GUID `D` format, making the dot-delimited
frame unambiguous. The signature header is lowercase `sha256=<hex>`. Binding the timestamp and
delivery ID to the exact payload detects mutation and, together with an enforced timestamp window,
limits replay but does not prevent it. Receivers compare signatures in constant time.

The standalone simulator can be run locally with its clearly synthetic Development secret:

```powershell
dotnet run --project src/RelayForge.UnstableReceiver
```

Configure its bounded failure scenario with `PUT /operations/scenario`; each reconfiguration
atomically clears the prior attempt counters. Status codes 400-599 are intentionally accepted so
the simulator can model both terminal and transient failures. Send signed raw bodies to
`POST /webhooks/relayforge`, and inspect attempts at `GET /operations/deliveries/{deliveryId}`.

Delivery failures use five total attempts by default. Transient failures are scheduled by PostgreSQL with capped exponential backoff and jitter; permanent failures and exhausted transient failures are dead-lettered. Operators can list sanitized dead letters at `GET /api/dead-letters` (manual replay is intentionally not available yet).
For any non-Development environment set `Receiver__SigningSecret` through configuration or the
environment. The simulator's state is deliberately thread-safe but in-memory and process-local: it
is a demo receiver, not a broker, durable queue, or delivery source of truth.

## Planned capabilities

- Idempotent event ingestion
- Durable at-least-once delivery
- Outbound HMAC signing in the delivery worker
- Retries with exponential backoff and jitter
- Dead-letter queue and manual replay
- Operational dashboard and OpenTelemetry

## Status

Early development. The repository will remain runnable at each published milestone.

