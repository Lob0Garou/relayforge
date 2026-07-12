using Microsoft.EntityFrameworkCore;
using RelayForge.Domain.Events;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.Infrastructure.Delivery;

public sealed record DeliveryLease(Guid DeliveryId, Guid LeaseId, uint FencingToken, DateTimeOffset LeaseExpiresAt, int AttemptNumber, string Payload, Uri Url, TimeSpan Timeout, string ProtectedSecret);

public sealed class DeliveryLeaseRepository(IDbContextFactory<RelayForgeDbContext> factory, TimeProvider clock)
{
    public async Task<IReadOnlyList<DeliveryLease>> ClaimAsync(int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow(); var leaseId = Guid.NewGuid();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ids = await db.Database.SqlQuery<Guid>($"""
            WITH candidates AS (
              SELECT "Id" FROM deliveries
              WHERE "Status" IN ('Pending', 'Replayed') OR ("Status" = 'Processing' AND lease_expires_at <= {now})
              ORDER BY "CreatedAt" FOR UPDATE SKIP LOCKED LIMIT {batchSize}
            )
            UPDATE deliveries d SET "Status" = 'Processing', lease_id = {leaseId}, lease_expires_at = {now + leaseDuration},
              attempt_count = attempt_count + 1, version = version + 1
            FROM candidates c WHERE d."Id" = c."Id" RETURNING d."Id" AS "Value"
            """).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (ids.Count == 0) return [];
        var deliveryIds = ids.Select(x => new DeliveryId(x)).ToArray();
        return await db.Deliveries.AsNoTracking().Where(x => deliveryIds.Contains(x.Id))
            .Join(db.IncomingEvents, d => d.EventId, e => e.Id, (d, e) => new { d, e })
            .Join(db.WebhookEndpoints, x => x.d.EndpointId, e => e.Id, (x, endpoint) => new DeliveryLease(x.d.Id.Value, x.d.LeaseId!.Value, x.d.Version, x.d.LeaseExpiresAt!.Value, x.d.AttemptCount, x.e.Payload, endpoint.Url, endpoint.Timeout, endpoint.ProtectedSecret))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> FinalizeAsync(DeliveryLease lease, DateTimeOffset startedAt, int? statusCode, string? error, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var completedAt = clock.GetUtcNow(); var success = statusCode is >= 200 and < 300;
        var deliveryId = new DeliveryId(lease.DeliveryId);
        var affected = await db.Deliveries.Where(x => x.Id == deliveryId && x.Status == DeliveryStatus.Processing && x.LeaseId == lease.LeaseId && x.Version == lease.FencingToken && x.LeaseExpiresAt > completedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, success ? DeliveryStatus.Delivered : DeliveryStatus.RetryScheduled)
                .SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.NextAttemptAt, success ? null : completedAt).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        if (affected == 0) { await transaction.RollbackAsync(cancellationToken); return false; }
        db.DeliveryAttempts.Add(DeliveryAttempt.Create(new(lease.DeliveryId), lease.AttemptNumber, startedAt, completedAt,
            success ? DeliveryAttemptOutcome.Delivered : DeliveryAttemptOutcome.Failed, statusCode, error));
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return true;
    }

    public async Task<bool> ReleaseAsync(DeliveryLease lease, DateTimeOffset? attemptStartedAt, string reason, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow(); var deliveryId = new DeliveryId(lease.DeliveryId); var attempted = attemptStartedAt.HasValue;
        var affected = await db.Deliveries.Where(x => x.Id == deliveryId && x.Status == DeliveryStatus.Processing && x.LeaseId == lease.LeaseId && x.Version == lease.FencingToken && x.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, attempted ? DeliveryStatus.RetryScheduled : DeliveryStatus.Pending)
                .SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.NextAttemptAt, attempted ? now : null).SetProperty(x => x.Version, x => x.Version + 1), cancellationToken);
        if (affected == 0) { await transaction.RollbackAsync(cancellationToken); return false; }
        if (attemptStartedAt is { } startedAt)
        {
            db.DeliveryAttempts.Add(DeliveryAttempt.Create(deliveryId, lease.AttemptNumber, startedAt, now, DeliveryAttemptOutcome.Failed, null, reason));
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken); return true;
    }
}
