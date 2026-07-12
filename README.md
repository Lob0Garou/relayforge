# RelayForge

## Event ingestion idempotency

`POST /api/events` uses a single-tenant, globally unique `Idempotency-Key` in the MVP. Replays with the same endpoint, event type, and semantically equivalent JSON payload return the original event and delivery IDs. Reusing the key for any different endpoint, event type, or payload returns `409 Conflict`.

Reliable webhook delivery and replay platform built with .NET 8 and React.

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

## Planned capabilities

- Idempotent event ingestion
- Durable at-least-once delivery
- HMAC signatures
- Retries with exponential backoff and jitter
- Dead-letter queue and manual replay
- Operational dashboard and OpenTelemetry

## Status

Early development. The repository will remain runnable at each published milestone.

