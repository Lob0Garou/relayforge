using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetryDuePolling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_deliveries_retry_due",
                table: "deliveries",
                columns: new[] { "next_attempt_at", "CreatedAt" },
                filter: "\"Status\" = 'RetryScheduled'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_deliveries_retry_due",
                table: "deliveries");
        }
    }
}
