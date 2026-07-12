# RelayForge

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

## Planned capabilities

- Idempotent event ingestion
- Durable at-least-once delivery
- HMAC signatures
- Retries with exponential backoff and jitter
- Dead-letter queue and manual replay
- Operational dashboard and OpenTelemetry

## Status

Early development. The repository will remain runnable at each published milestone.

