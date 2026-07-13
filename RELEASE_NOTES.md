# Release notes

## v0.1.0 - Release candidate

Initial public release candidate of RelayForge, a self-hosted webhook delivery control plane.

### Included

- durable idempotent event ingestion and PostgreSQL-backed delivery leases;
- HMAC-signed delivery, retry scheduling, dead-letter listing, and replay;
- destination validation and bounded sanitized diagnostics;
- operator API, React dashboard, health endpoints, and telemetry instruments;
- Testcontainers integration suite, Playwright operator flow, Slopwatch gate, and GitHub Actions CI;
- non-root multi-stage images, dedicated migration job, and one-command local Compose demo.

### Deployment warning

The v0.1 operator APIs have no authentication and the Compose credentials are synthetic. The provided stack is for isolated local evaluation, not internet-facing production use.
