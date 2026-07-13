# Architecture

RelayForge separates database evolution, API ingress, asynchronous delivery, and the operator console while keeping PostgreSQL as the source of truth.

## Runtime components

- **MigrationService** applies EF Core migrations through the provider execution strategy and exits nonzero on failure. API startup waits for its successful completion.
- **API** owns endpoint registration, event ingestion, health checks, operational queries, dead letters, and replay. Its hosted worker leases due deliveries from PostgreSQL.
- **PostgreSQL** stores endpoints, canonical event payloads, deliveries, attempts, leases, and replay operations.
- **Web** is a static React application served by unprivileged nginx. `/api` and `/health` are reverse-proxied to the API.
- **UnstableReceiver** is a development-only failure simulator, never a durable broker or production receiver.

## Delivery path

1. The client registers an endpoint and receives its signing secret once.
2. `POST /api/events` validates and canonicalizes JSON, then creates the event and pending delivery in one transaction.
3. The worker claims a time-bounded lease with PostgreSQL locking.
4. The dispatcher resolves and validates the destination, signs the exact payload, and sends without redirects or environment proxies.
5. The result becomes succeeded, scheduled for retry, or dead-lettered. Sanitized response snippets are bounded before persistence.

Crashes can cause a lease to expire and the delivery to be attempted again. This is the basis of the at-least-once model.

## Data protection

Development stores Data Protection keys in a persistent named volume. Production-like environments require a shared persistent keyring and X509 key encryption. Losing the keyring makes stored endpoint secrets undecryptable.
