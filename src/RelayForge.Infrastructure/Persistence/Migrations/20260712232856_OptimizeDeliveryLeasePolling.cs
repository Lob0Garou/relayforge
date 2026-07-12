using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeDeliveryLeasePolling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deliveries_polling",
                table: "deliveries");

            migrationBuilder.DropIndex(
                name: "ix_deliveries_status_created",
                table: "deliveries");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_expired_leases",
                table: "deliveries",
                columns: new[] { "lease_expires_at", "CreatedAt" },
                filter: "\"Status\" = 'Processing'");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_ready",
                table: "deliveries",
                columns: new[] { "Status", "CreatedAt" },
                filter: "\"Status\" IN ('Pending', 'Replayed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deliveries_expired_leases",
                table: "deliveries");

            migrationBuilder.DropIndex(
                name: "ix_deliveries_ready",
                table: "deliveries");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_polling",
                table: "deliveries",
                columns: new[] { "Status", "next_attempt_at", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_status_created",
                table: "deliveries",
                columns: new[] { "Status", "CreatedAt" });
        }
    }
}
