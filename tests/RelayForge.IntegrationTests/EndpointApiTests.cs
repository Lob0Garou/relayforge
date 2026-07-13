using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
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
        var protector = fixture.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RelayForge.WebhookEndpointSecrets.v1");
        Assert.Equal(created.Secret, protector.Unprotect(stored.ProtectedSecret));

        await using var restartedFactory = new RelayForgeApiFactory(fixture.ConnectionString, fixture.Factory.KeysPath);
        var restartedProtector = restartedFactory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("RelayForge.WebhookEndpointSecrets.v1");
        Assert.Equal(created.Secret, restartedProtector.Unprotect(stored.ProtectedSecret));
    }

    [Fact]
    public async Task Get_returns_paginated_contract_without_secret_fields()
    {
        await fixture.ResetAsync();
        using var client = fixture.Factory.CreateClient();
        using var creation = await client.PostAsJsonAsync("/api/endpoints", new { name = "Orders", url = "https://example.com/hooks", timeoutSeconds = 30 });
        creation.EnsureSuccessStatusCode();
        var created = await creation.Content.ReadFromJsonAsync<CreatedEndpointResponse>();
        Assert.NotNull(created);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var protectedSecret = await scope.ServiceProvider.GetRequiredService<RelayForgeDbContext>().WebhookEndpoints
            .Where(endpoint => endpoint.Id == new RelayForge.Domain.Endpoints.WebhookEndpointId(created.Id))
            .Select(endpoint => endpoint.ProtectedSecret)
            .SingleAsync();

        using var response = await client.GetAsync("/api/endpoints?page=1&pageSize=10");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var page = JsonSerializer.Deserialize<EndpointPageResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(page);
        var item = Assert.Single(page.Items);
        Assert.Equal("Orders", item.Name);
        Assert.Equal("https://example.com/hooks", item.Url);
        Assert.Equal(1, page.Page);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(1, page.TotalCount);

        using var document = JsonDocument.Parse(json);
        AssertJsonHasNoSecretContract(document.RootElement, created.Secret, protectedSecret);
    }

    [Fact]
    public async Task Get_rejects_page_offset_overflow_as_validation_problem()
    {
        using var client = fixture.Factory.CreateClient();
        using var response = await client.GetAsync($"/api/endpoints?page={int.MaxValue}&pageSize=100");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
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

        command.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'ix_webhook_endpoints_created_id'";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private sealed record CreatedEndpointResponse(Guid Id, string Name, string Url, int TimeoutSeconds, bool IsActive, string Secret);
    private sealed record EndpointItemResponse(Guid Id, string Name, string Url, int TimeoutSeconds, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record EndpointPageResponse(IReadOnlyList<EndpointItemResponse> Items, int Page, int PageSize, int TotalCount);

    private static void AssertJsonHasNoSecretContract(JsonElement element, params string[] forbiddenValues)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.False(string.Equals("secret", property.Name, StringComparison.OrdinalIgnoreCase));
                Assert.False(string.Equals("protectedSecret", property.Name, StringComparison.OrdinalIgnoreCase));
                AssertJsonHasNoSecretContract(property.Value, forbiddenValues);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) AssertJsonHasNoSecretContract(item, forbiddenValues);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            foreach (var forbiddenValue in forbiddenValues)
                Assert.DoesNotContain(forbiddenValue, element.GetString()!, StringComparison.Ordinal);
        }
    }
}
