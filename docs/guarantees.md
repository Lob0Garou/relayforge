# Delivery guarantees

## Guarantees within the v0.1 boundary

- An accepted event and its initial delivery are durably committed together in PostgreSQL.
- A valid global `Idempotency-Key` returns the original IDs for semantically equivalent JSON; conflicting reuse returns `409`.
- Pending and retry-due deliveries survive API restarts.
- Work is leased so concurrent workers do not intentionally process the same delivery at the same instant.
- Transient failures are retried with capped exponential backoff and jitter up to the configured total-attempt limit.
- Every outbound request is HMAC-signed over the timestamp, canonical delivery ID, and exact body bytes.

## Explicit non-guarantees

- **Not exactly once.** A crash or timeout after the receiver processes a request but before RelayForge records success can cause another attempt.
- No global or per-endpoint ordering guarantee.
- No transactional coupling with receiver state.
- No guarantee that a destination remains on the same IP for an existing pooled connection; validation repeats when a new connection is established.
- No production SLA, multi-region durability, or disaster recovery promise in v0.1.

Receivers must deduplicate by `RelayForge-Delivery-Id`, validate the signature in constant time, enforce a timestamp window, and make their own processing idempotent.
