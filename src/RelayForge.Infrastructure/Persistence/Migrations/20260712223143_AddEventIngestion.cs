using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incoming_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incoming_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_incoming_events_webhook_endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_deliveries_incoming_events_EventId",
                        column: x => x.EventId,
                        principalTable: "incoming_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_deliveries_webhook_endpoints_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_EndpointId",
                table: "deliveries",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_EventId",
                table: "deliveries",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_status_created",
                table: "deliveries",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_incoming_events_EndpointId",
                table: "incoming_events",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "ix_incoming_events_status_created",
                table: "incoming_events",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_incoming_events_idempotency_key",
                table: "incoming_events",
                column: "IdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deliveries");

            migrationBuilder.DropTable(
                name: "incoming_events");
        }
    }
}
