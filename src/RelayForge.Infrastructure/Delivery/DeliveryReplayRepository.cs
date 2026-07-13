using Microsoft.EntityFrameworkCore;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.Infrastructure.Delivery;

public enum ReplayResultKind { Accepted, NotFound, NotDeadLettered, EndpointInactive }
public sealed record ReplayResult(ReplayResultKind Kind, Guid? ReplayId = null, Guid? DeliveryId = null);

public sealed class DeliveryReplayRepository(IDbContextFactory<RelayForgeDbContext> factory)
{
    public async Task<ReplayResult> ReplayAsync(Guid deliveryId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var rows = await db.Database.SqlQuery<ReplayRow>($"""
            WITH locked AS (
              SELECT d."Id", d."Status", d.attempt_count, w.is_active,
                (SELECT count(*)::int FROM delivery_replays r WHERE r."DeliveryId" = d."Id") + 1 AS cycle,
                clock_timestamp() AS requested_at
              FROM deliveries d JOIN webhook_endpoints w ON w.id = d."EndpointId"
              WHERE d."Id" = {deliveryId} FOR UPDATE OF d
            ), inserted AS (
              INSERT INTO delivery_replays ("Id", "DeliveryId", requested_at, starting_attempt_number, cycle_number)
              SELECT gen_random_uuid(), "Id", requested_at, attempt_count + 1, cycle FROM locked
              WHERE "Status" = 'DeadLettered' AND is_active
              RETURNING "Id" AS replay_id, "DeliveryId"
            ), updated AS (
              UPDATE deliveries d SET "Status" = 'Replayed', next_attempt_at = l.requested_at,
                lease_id = NULL, lease_expires_at = NULL, version = version + 1
              FROM locked l, inserted i WHERE d."Id" = l."Id" AND i."DeliveryId" = d."Id"
              RETURNING d."Id"
            )
            SELECT (SELECT "Status" FROM locked) AS "PriorStatus",
              (SELECT is_active FROM locked) AS "EndpointActive",
              (SELECT replay_id FROM inserted) AS "ReplayId",
              (SELECT "Id" FROM updated) AS "DeliveryId"
            """).ToListAsync(token);
        await tx.CommitAsync(token);
        if (rows.Count == 0 || rows[0].PriorStatus is null) return new(ReplayResultKind.NotFound);
        var row = rows[0];
        if (row.EndpointActive == false) return new(ReplayResultKind.EndpointInactive);
        if (row.PriorStatus != "DeadLettered") return new(ReplayResultKind.NotDeadLettered);
        return new(ReplayResultKind.Accepted, row.ReplayId, row.DeliveryId);
    }

    private sealed record ReplayRow(string? PriorStatus, bool? EndpointActive, Guid? ReplayId, Guid? DeliveryId);
}
