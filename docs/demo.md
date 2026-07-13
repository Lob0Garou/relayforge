# Local demo

## Start

```powershell
docker compose up --build -d --wait
./scripts/demo.ps1
```

The script creates an endpoint, captures its generated signing secret without printing it, rotates the receiver's in-memory Development secret, removes it from the response object, configures two synthetic failures, ingests an event, and polls until the third attempt is delivered. It does not write the secret to disk.

`PUT /control/secret` exists only when the unstable receiver runs in `Development`. It is intentionally unauthenticated local-demo plumbing, never returns or logs the secret, and must not be exposed to another machine or used in production.

Useful surfaces:

- dashboard: <http://localhost:5173>
- API readiness: <http://localhost:5000/health/ready>
- receiver liveness from the Compose network: `http://unstable-receiver:8080/health/live`

## Inspect and stop

```powershell
docker compose ps
docker compose logs api unstable-receiver
docker compose down -v
```

Do not use `docker system prune` or remove unrelated containers. Compose project name `relayforge` scopes the stack resources.
