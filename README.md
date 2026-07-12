# RelayForge

Reliable webhook delivery and replay platform built with .NET 8 and React.

RelayForge is being developed in public through small, verifiable milestones. The first executable foundation is available.

## Quickstart

```powershell
dotnet tool restore
dotnet build RelayForge.slnx
dotnet test RelayForge.slnx
dotnet run --project src/RelayForge.Api
```

The live health endpoint is available at `/health/live`.

## Planned capabilities

- Idempotent event ingestion
- Durable at-least-once delivery
- HMAC signatures
- Retries with exponential backoff and jitter
- Dead-letter queue and manual replay
- Operational dashboard and OpenTelemetry

## Status

Early development. The repository will remain runnable at each published milestone.

