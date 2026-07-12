using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelayForge.Domain.Events;

namespace RelayForge.Infrastructure.Persistence;

public sealed class IncomingEventConfiguration : IEntityTypeConfiguration<IncomingEvent>
{
    public void Configure(EntityTypeBuilder<IncomingEvent> builder)
    {
        builder.ToTable("incoming_events"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasConversion(x => x.Value, x => new(x)).ValueGeneratedNever();
        builder.Property(x => x.EndpointId).HasConversion(x => x.Value, x => new(x));
        builder.Property(x => x.EventType).HasMaxLength(IncomingEvent.MaxEventTypeLength).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_incoming_events_idempotency_key");
        builder.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_incoming_events_status_created");
        builder.HasOne<RelayForge.Domain.Endpoints.WebhookEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Delivery).WithOne().HasForeignKey<RelayForge.Domain.Events.Delivery>(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeliveryConfiguration : IEntityTypeConfiguration<RelayForge.Domain.Events.Delivery>
{
    public void Configure(EntityTypeBuilder<RelayForge.Domain.Events.Delivery> builder)
    {
        builder.ToTable("deliveries"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasConversion(x => x.Value, x => new(x)).ValueGeneratedNever();
        builder.Property(x => x.EventId).HasConversion(x => x.Value, x => new(x));
        builder.Property(x => x.EndpointId).HasConversion(x => x.Value, x => new(x));
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.LeaseId).HasColumnName("lease_id");
        builder.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count");
        builder.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
        builder.Property(x => x.Version).IsConcurrencyToken().HasColumnName("version");
        builder.HasMany(x => x.Attempts).WithOne().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.EventId).IsUnique();
        builder.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_deliveries_ready").HasFilter("\"Status\" IN ('Pending', 'Replayed')");
        builder.HasIndex(x => new { x.LeaseExpiresAt, x.CreatedAt }).HasDatabaseName("ix_deliveries_expired_leases").HasFilter("\"Status\" = 'Processing'");
        builder.HasOne<RelayForge.Domain.Endpoints.WebhookEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> builder)
    {
        builder.ToTable("delivery_attempts"); builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.DeliveryId).HasConversion(x => x.Value, x => new(x));
        builder.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Error).HasMaxLength(1000);
        builder.HasIndex(x => new { x.DeliveryId, x.Number }).IsUnique();
    }
}
