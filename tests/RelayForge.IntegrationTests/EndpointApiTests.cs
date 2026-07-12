using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RelayForge.Infrastructure.Persistence;

namespace RelayForge.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class EndpointApiTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Post_persists_endpoint_and_returns_secret_once()
    {
        await fixture.ResetAsync();
        using var client = fixture.Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/endpoints", new { name = "Orders", url = "https://example.com/hooks", timeoutSeconds = 30 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedEndpointResponse>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.False(string.IsNullOrWhiteSpace(created.Secret));
        Assert.Equal($"/api/endpoints/{created.Id}", response.Headers.Location?.OriginalString);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>().WebhookEndpoints.SingleAsync(endpoint => endpoint.Id == new RelayForge.Domain.Endpoints.WebhookEndpointId(created.Id));
        Assert.NotEqual(created.Secret, stored.ProtectedSecret);
    }

    [Fact]
    public async Task Get_returns_paginated_contract_without_secret_fields()
    {
        await fixture.ResetAsync();
        using var client = fixture.Factory.CreateClient();
        using var creation = await client.PostAsJsonAsync("/api/endpoints", new { name = "Orders", url = "https://example.com/hooks", timeoutSeconds = 30 });
        creation.EnsureSuccessStatusCode();

        var page = await client.GetFromJsonAsync<EndpointPageResponse>("/api/endpoints?page=1&pageSize=10");
        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal("Orders", item.Name);
        Assert.Equal("https://example.com/hooks", item.Url);
        Assert.Equal(1, page.Page);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Migration_and_database_constraints_are_applied()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_constraint WHERE conname IN ('ck_webhook_endpoints_timeout', 'ck_webhook_endpoints_url_scheme')";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    private sealed record CreatedEndpointResponse(Guid Id, string Name, string Url, int TimeoutSeconds, bool IsActive, string Secret);
    private sealed record EndpointItemResponse(Guid Id, string Name, string Url, int TimeoutSeconds, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record EndpointPageResponse(IReadOnlyList<EndpointItemResponse> Items, int Page, int PageSize, int TotalCount);
}
