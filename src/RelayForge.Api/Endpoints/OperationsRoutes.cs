using Microsoft.EntityFrameworkCore;
using RelayForge.Domain.Events;
using RelayForge.Infrastructure;
using RelayForge.Infrastructure.Delivery;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.Api.Endpoints;

public static class OperationsRoutes
{
    private const int RecentHours = 24;
    public static IEndpointRouteBuilder MapOperationsRoutes(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/operations/overview", OverviewAsync);
        routes.MapGet("/api/events", EventsAsync);
        routes.MapGet("/api/deliveries/{id:guid}", DetailAsync);
        routes.MapPost("/api/dead-letters/{deliveryId:guid}/replay", ReplayAsync);
        return routes;
    }

    private static async Task<IResult> OverviewAsync(IDbContextFactory<RelayForgeDbContext> factory, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token); var now = DateTimeOffset.UtcNow; var since = now.AddHours(-RecentHours);
        var counts = await db.Deliveries.AsNoTracking().GroupBy(x => x.Status).Select(x => new { status = x.Key.ToString(), count = x.Count() }).ToListAsync(token);
        var ready = await db.Deliveries.AsNoTracking().CountAsync(x => x.Status == DeliveryStatus.Pending || x.Status == DeliveryStatus.Replayed || x.Status == DeliveryStatus.RetryScheduled && x.NextAttemptAt <= now, token);
        RelayForgeTelemetry.SetBacklog(ready);
        var recent = await db.DeliveryAttempts.AsNoTracking().Where(x => x.CompletedAt >= since).GroupBy(_ => 1).Select(x => new { total = x.Count(), success = x.Count(a => a.Outcome == DeliveryAttemptOutcome.Delivered), latency = x.Average(a => (double)a.DurationMilliseconds) }).SingleOrDefaultAsync(token);
        return Results.Ok(new { generatedAt = now, recentWindowHours = RecentHours, counts, backlogReadyDue = ready, deadLetterCount = counts.Where(x => x.status == nameof(DeliveryStatus.DeadLettered)).Sum(x => x.count), successRate = recent == null || recent.total == 0 ? 0 : (double)recent.success / recent.total, failureRate = recent == null || recent.total == 0 ? 0 : (double)(recent.total - recent.success) / recent.total, averageLatencyMilliseconds = recent?.latency ?? 0 });
    }

    private static async Task<IResult> EventsAsync(int? page, int? pageSize, string? type, string? status, Guid? endpointId, IDbContextFactory<RelayForgeDbContext> factory, CancellationToken token)
    {
        var p = Math.Max(1, page ?? 1); var size = Math.Clamp(pageSize ?? 20, 1, 100);
        if (!string.IsNullOrWhiteSpace(status) && !Enum.TryParse<DeliveryStatus>(status, true, out _)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Unknown delivery status."] });
        await using var db = await factory.CreateDbContextAsync(token);
        var endpointKey = endpointId is { } value ? new RelayForge.Domain.Endpoints.WebhookEndpointId(value) : default;
        var query = from e in db.IncomingEvents.AsNoTracking() join d in db.Deliveries.AsNoTracking() on e.Id equals d.EventId join w in db.WebhookEndpoints.AsNoTracking() on e.EndpointId equals w.Id where (type == null || e.EventType == type) && (endpointId == null || e.EndpointId == endpointKey) select new { e, d, w };
        if (!string.IsNullOrWhiteSpace(status)) { var statusValue = Enum.Parse<DeliveryStatus>(status, true); query = query.Where(x => x.d.Status == statusValue); }
        var total = await query.CountAsync(token); var skip = (int)Math.Min((long)(p - 1) * size, int.MaxValue);
        var items = await query.OrderByDescending(x => x.e.CreatedAt).ThenBy(x => x.e.Id).Skip(skip).Take(size).Select(x => new { eventId = x.e.Id.Value, type = x.e.EventType, receivedAt = x.e.CreatedAt, deliveryId = x.d.Id.Value, status = x.d.Status.ToString(), attemptCount = x.d.AttemptCount, endpoint = new { id = x.w.Id.Value, name = x.w.Name } }).ToListAsync(token);
        return Results.Ok(new { page = p, pageSize = size, total, items });
    }

    private static async Task<IResult> DetailAsync(Guid id, IDbContextFactory<RelayForgeDbContext> factory, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token); var key = new DeliveryId(id);
        var metadata = await (from d in db.Deliveries.AsNoTracking() join e in db.IncomingEvents.AsNoTracking() on d.EventId equals e.Id join w in db.WebhookEndpoints.AsNoTracking() on d.EndpointId equals w.Id where d.Id == key select new { deliveryId = d.Id.Value, status = d.Status.ToString(), attemptCount = d.AttemptCount, createdAt = d.CreatedAt, @event = new { id = e.Id.Value, type = e.EventType, receivedAt = e.CreatedAt }, endpoint = new { id = w.Id.Value, name = w.Name, active = w.IsActive } }).SingleOrDefaultAsync(token);
        if (metadata is null) return Results.NotFound();
        var attemptCount = await db.DeliveryAttempts.AsNoTracking().CountAsync(x => x.DeliveryId == key, token);
        var replayCount = await db.DeliveryReplays.AsNoTracking().CountAsync(x => x.DeliveryId == key, token);
        var attempts = await db.DeliveryAttempts.AsNoTracking().Where(x => x.DeliveryId == key).OrderByDescending(x => x.Number).ThenByDescending(x => x.Id).Take(100).Select(x => new { id = x.Id, number = x.Number, startedAt = x.StartedAt, completedAt = x.CompletedAt, status = x.Outcome.ToString(), durationMilliseconds = x.DurationMilliseconds, httpStatus = x.HttpStatusCode, errorCode = x.Error }).ToListAsync(token);
        var replays = await db.DeliveryReplays.AsNoTracking().Where(x => x.DeliveryId == key).OrderByDescending(x => x.CycleNumber).ThenByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id).Take(50).Select(x => new { replayId = x.Id.Value, cycleNumber = x.CycleNumber, startingAttemptNumber = x.StartingAttemptNumber, requestedAt = x.RequestedAt }).ToListAsync(token);
        return Results.Ok(new { delivery = metadata, attemptHistoryCount = attemptCount, replayHistoryCount = replayCount, historyTruncated = attemptCount > 100 || replayCount > 50, attempts, replays });
    }

    private static async Task<IResult> ReplayAsync(Guid deliveryId, DeliveryReplayRepository repository, CancellationToken token)
    {
        using var activity = RelayForgeTelemetry.StartReplay(deliveryId); var result = await repository.ReplayAsync(deliveryId, token); RelayForgeTelemetry.RecordReplay(result.Kind.ToString());
        return result.Kind switch { ReplayResultKind.Accepted => Results.Accepted($"/api/deliveries/{deliveryId}", new { replayId = result.ReplayId, deliveryId = result.DeliveryId, status = "Replayed" }), ReplayResultKind.NotFound => Results.NotFound(), ReplayResultKind.EndpointInactive => Results.Conflict(new { code = "endpoint_inactive", detail = "Inactive endpoints cannot be replayed." }), _ => Results.Conflict(new { code = "delivery_not_dead_lettered", detail = "Only dead-lettered deliveries can be replayed." }) };
    }
}
