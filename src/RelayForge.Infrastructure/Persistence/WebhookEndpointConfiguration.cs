using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelayForge.Domain.Endpoints;

namespace RelayForge.Infrastructure.Persistence;

internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("webhook_endpoints", table =>
        {
            table.HasCheckConstraint("ck_webhook_endpoints_timeout", $"timeout_seconds BETWEEN {WebhookEndpoint.MinTimeoutSeconds} AND {WebhookEndpoint.MaxTimeoutSeconds}");
            table.HasCheckConstraint("ck_webhook_endpoints_url_scheme", "url LIKE 'http://%' OR url LIKE 'https://%'");
        });
        builder.HasKey(endpoint => endpoint.Id);
        builder.Property(endpoint => endpoint.Id).HasConversion(id => id.Value, value => new WebhookEndpointId(value)).ValueGeneratedNever().HasColumnName("id");
        builder.Property(endpoint => endpoint.Name).HasMaxLength(WebhookEndpoint.MaxNameLength).IsRequired().HasColumnName("name");
        builder.Property(endpoint => endpoint.Url).HasConversion(url => url.AbsoluteUri, value => new Uri(value)).HasMaxLength(WebhookEndpoint.MaxUrlLength).IsRequired().HasColumnName("url");
        builder.Property(endpoint => endpoint.Timeout).HasConversion(timeout => (int)timeout.TotalSeconds, seconds => TimeSpan.FromSeconds(seconds)).HasColumnName("timeout_seconds");
        builder.Property(endpoint => endpoint.IsActive).HasColumnName("is_active");
        builder.Property(endpoint => endpoint.ProtectedSecret).HasMaxLength(4096).IsRequired().HasColumnName("protected_secret");
        builder.Property(endpoint => endpoint.CreatedAt).HasColumnName("created_at");
        builder.Property(endpoint => endpoint.UpdatedAt).HasColumnName("updated_at");
        builder.HasIndex(endpoint => new { endpoint.IsActive, endpoint.CreatedAt }).HasDatabaseName("ix_webhook_endpoints_active_created");
    }
}
