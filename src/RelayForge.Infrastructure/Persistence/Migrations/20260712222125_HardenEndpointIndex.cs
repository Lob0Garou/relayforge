using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenEndpointIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_webhook_endpoints_active_created",
                table: "webhook_endpoints");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_created_id",
                table: "webhook_endpoints",
                columns: new[] { "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_webhook_endpoints_created_id",
                table: "webhook_endpoints");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_active_created",
                table: "webhook_endpoints",
                columns: new[] { "is_active", "created_at" });
        }
    }
}
