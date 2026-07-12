using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableDeliveryLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at",
                table: "deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                table: "deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "deliveries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_attempts_deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_polling",
                table: "deliveries",
                columns: new[] { "Status", "next_attempt_at", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_DeliveryId_Number",
                table: "delivery_attempts",
                columns: new[] { "DeliveryId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_attempts");

            migrationBuilder.DropIndex(
                name: "ix_deliveries_polling",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "lease_id",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "version",
                table: "deliveries");
        }
    }
}
