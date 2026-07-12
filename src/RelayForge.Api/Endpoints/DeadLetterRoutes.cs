using Microsoft.EntityFrameworkCore;
using RelayForge.Domain.Events;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.Api.Endpoints;

public static class DeadLetterRoutes
{
    public static IEndpointRouteBuilder MapDeadLetterRoutes(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dead-letters", async (int? page, int? pageSize, IDbContextFactory<RelayForgeDbContext> factory, CancellationToken token) =>
        {
            var safePage = Math.Max(1, page ?? 1); var safeSize = Math.Clamp(pageSize ?? 20, 1, 100);
            await using var db = await factory.CreateDbContextAsync(token);
            var query = db.Deliveries.AsNoTracking().Where(x => x.Status == DeliveryStatus.DeadLettered);
            var total = await query.CountAsync(token);
            var skip = (int)Math.Min((long)(safePage - 1) * safeSize, int.MaxValue);
            var items = await (from delivery in query
                join incomingEvent in db.IncomingEvents.AsNoTracking() on delivery.EventId equals incomingEvent.Id
                join endpoint in db.WebhookEndpoints.AsNoTracking() on delivery.EndpointId equals endpoint.Id
                orderby delivery.CreatedAt descending, delivery.Id
                select new { delivery, incomingEvent, endpoint })
                .Skip(skip).Take(safeSize)
                .Select(x => new
                {
                    deliveryId = x.delivery.Id.Value,
                    @event = new { id = x.incomingEvent.Id.Value, type = x.incomingEvent.EventType },
                    endpoint = new { id = x.endpoint.Id.Value, name = x.endpoint.Name },
                    attemptsCount = x.delivery.AttemptCount,
                    reason = x.delivery.Attempts.OrderByDescending(a => a.Number).Select(a => a.Error).FirstOrDefault(),
                    createdAt = x.delivery.CreatedAt,
                    lastAttemptAt = x.delivery.Attempts.OrderByDescending(a => a.Number).Select(a => (DateTimeOffset?)a.CompletedAt).FirstOrDefault()
                }).ToListAsync(token);
            return Results.Ok(new { page = safePage, pageSize = safeSize, total, items });
        });
        return routes;
    }
}
