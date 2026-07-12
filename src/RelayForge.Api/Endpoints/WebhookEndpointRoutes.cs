using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using RelayForge.Domain.Endpoints;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.Api.Endpoints;

public static class WebhookEndpointRoutes
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/endpoints");
        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        return routes;
    }

    private static async Task<Results<Created<CreatedEndpointResponse>, ValidationProblem>> CreateAsync(
        CreateEndpointRequest request,
        RelayForgeDbContext db,
        IDataProtectionProvider protectionProvider,
        CancellationToken cancellationToken)
    {
        var secret = GenerateSecret();
        var protectedSecret = protectionProvider.CreateProtector("RelayForge.WebhookEndpointSecrets.v1").Protect(secret);
        var result = WebhookEndpoint.Create(request.Name, request.Url, TimeSpan.FromSeconds(request.TimeoutSeconds), protectedSecret);
        if (!result.IsSuccess)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [result.Error.Code] = [result.Error.Description] });
        }

        db.WebhookEndpoints.Add(result.Value);
        await db.SaveChangesAsync(cancellationToken);
        var endpoint = result.Value;
        return TypedResults.Created($"/api/endpoints/{endpoint.Id.Value}", new CreatedEndpointResponse(
            endpoint.Id.Value, endpoint.Name, endpoint.Url.AbsoluteUri, (int)endpoint.Timeout.TotalSeconds, endpoint.IsActive, secret));
    }

    private static async Task<Results<Ok<EndpointPageResponse>, ValidationProblem>> ListAsync(
        RelayForgeDbContext db,
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > MaxPageSize)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["pagination"] = [$"Page must be positive and pageSize must be between 1 and {MaxPageSize}."] });
        }

        var query = db.WebhookEndpoints.OrderBy(endpoint => endpoint.CreatedAt);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(endpoint => new EndpointRow(endpoint.Id, endpoint.Name, endpoint.Url, endpoint.Timeout, endpoint.IsActive, endpoint.CreatedAt, endpoint.UpdatedAt))
            .ToListAsync(cancellationToken);
        var items = rows.Select(row => new EndpointItemResponse(row.Id.Value, row.Name, row.Url.AbsoluteUri,
            (int)row.Timeout.TotalSeconds, row.IsActive, row.CreatedAt, row.UpdatedAt)).ToArray();
        return TypedResults.Ok(new EndpointPageResponse(items, page, pageSize, totalCount));
    }

    private static string GenerateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public sealed record CreateEndpointRequest(string Name, string Url, int TimeoutSeconds);
    public sealed record CreatedEndpointResponse(Guid Id, string Name, string Url, int TimeoutSeconds, bool IsActive, string Secret);
    public sealed record EndpointItemResponse(Guid Id, string Name, string Url, int TimeoutSeconds, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record EndpointPageResponse(IReadOnlyList<EndpointItemResponse> Items, int Page, int PageSize, int TotalCount);
    private sealed record EndpointRow(WebhookEndpointId Id, string Name, Uri Url, TimeSpan Timeout, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
