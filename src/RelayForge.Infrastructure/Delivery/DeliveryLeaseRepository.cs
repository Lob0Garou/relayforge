using Microsoft.EntityFrameworkCore;
using RelayForge.Domain.Events;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.Infrastructure.Delivery;

public sealed record DeliveryLease(Guid DeliveryId, Guid LeaseId, uint FencingToken, DateTimeOffset LeaseExpiresAt, int AttemptNumber, string Payload, Uri Url, TimeSpan Timeout, string ProtectedSecret);
public interface IDeliveryClaimObserver { Task SnapshotCapturedAsync(CancellationToken cancellationToken); }

public sealed class DeliveryLeaseRepository(IDbContextFactory<RelayForgeDbContext> factory, TimeProvider? processClock = null, IDeliveryClaimObserver? observer = null)
{
    public async Task<IReadOnlyList<DeliveryLease>> ClaimAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        _ = processClock; // Accepted for DI/test skew; lease authority remains PostgreSQL.
        var leaseId = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.Database.SqlQuery<DeliveryLeaseRow>($"""
            WITH candidates AS (
              SELECT "Id" FROM deliveries
              WHERE "Status" IN ('Pending', 'Replayed') OR ("Status" = 'Processing' AND lease_expires_at <= clock_timestamp())
              ORDER BY "CreatedAt" FOR UPDATE SKIP LOCKED LIMIT {batchSize}
            ), claimed AS (
              UPDATE deliveries d SET "Status" = 'Processing', lease_id = {leaseId},
                lease_expires_at = clock_timestamp() + {leaseDuration}, attempt_count = attempt_count + 1, version = version + 1
              FROM candidates c WHERE d."Id" = c."Id"
              RETURNING d."Id", d."EventId", d."EndpointId", d.lease_id, d.lease_expires_at, d.attempt_count, d.version
            )
            SELECT c."Id" AS "DeliveryId", c.lease_id AS "LeaseId", c.version AS "FencingToken",
              c.lease_expires_at AS "LeaseExpiresAt", c.attempt_count AS "AttemptNumber", e."Payload",
              w.url AS "Url", w.timeout_seconds AS "TimeoutSeconds", w.protected_secret AS "ProtectedSecret"
            FROM claimed c JOIN incoming_events e ON e."Id" = c."EventId"
              JOIN webhook_endpoints w ON w.id = c."EndpointId"
            WHERE c.lease_id = {leaseId}
            """).ToListAsync(cancellationToken);
        if (observer is not null) await observer.SnapshotCapturedAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows.Select(x => new DeliveryLease(x.DeliveryId, x.LeaseId, x.FencingToken, x.LeaseExpiresAt, x.AttemptNumber, x.Payload, new Uri(x.Url), TimeSpan.FromSeconds(x.TimeoutSeconds), x.ProtectedSecret)).ToArray();
    }

    public async Task<bool> FinalizeAsync(DeliveryLease lease, DateTimeOffset startedAt, int? statusCode, string? error, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken); await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var success = statusCode is >= 200 and < 300; var target = success ? "Delivered" : "RetryScheduled"; var deliveryId = new DeliveryId(lease.DeliveryId);
        var completed = await db.Database.SqlQuery<DateTimeOffset>($"""
            UPDATE deliveries SET "Status" = {target}, lease_id = NULL, lease_expires_at = NULL,
              next_attempt_at = CASE WHEN {success} THEN NULL ELSE clock_timestamp() END, version = version + 1
            WHERE "Id" = {lease.DeliveryId} AND "Status" = 'Processing' AND lease_id = {lease.LeaseId}
              AND version = {lease.FencingToken} AND lease_expires_at > clock_timestamp()
            RETURNING clock_timestamp() AS "Value"
            """).ToListAsync(cancellationToken);
        if (completed.Count == 0) { await transaction.RollbackAsync(cancellationToken); return false; }
        db.DeliveryAttempts.Add(DeliveryAttempt.Create(deliveryId, lease.AttemptNumber, startedAt, completed[0], success ? DeliveryAttemptOutcome.Delivered : DeliveryAttemptOutcome.Failed, statusCode, error));
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return true;
    }

    public async Task<bool> ReleaseAsync(DeliveryLease lease, DateTimeOffset? attemptStartedAt, string reason, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken); await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var attempted = attemptStartedAt.HasValue; var target = attempted ? "RetryScheduled" : "Pending"; var deliveryId = new DeliveryId(lease.DeliveryId);
        var completed = await db.Database.SqlQuery<DateTimeOffset>($"""
            UPDATE deliveries SET "Status" = {target}, lease_id = NULL, lease_expires_at = NULL,
              next_attempt_at = CASE WHEN {attempted} THEN clock_timestamp() ELSE NULL END, version = version + 1
            WHERE "Id" = {lease.DeliveryId} AND "Status" = 'Processing' AND lease_id = {lease.LeaseId}
              AND version = {lease.FencingToken} AND lease_expires_at > clock_timestamp()
            RETURNING clock_timestamp() AS "Value"
            """).ToListAsync(cancellationToken);
        if (completed.Count == 0) { await transaction.RollbackAsync(cancellationToken); return false; }
        if (attemptStartedAt is { } startedAt) { db.DeliveryAttempts.Add(DeliveryAttempt.Create(deliveryId, lease.AttemptNumber, startedAt, completed[0], DeliveryAttemptOutcome.Failed, null, reason)); await db.SaveChangesAsync(cancellationToken); }
        await transaction.CommitAsync(cancellationToken); return true;
    }

    private sealed record DeliveryLeaseRow(Guid DeliveryId, Guid LeaseId, uint FencingToken, DateTimeOffset LeaseExpiresAt, int AttemptNumber, string Payload, string Url, int TimeoutSeconds, string ProtectedSecret);
}
