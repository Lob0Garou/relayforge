using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayForge.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialEndpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    protected_secret = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_endpoints", x => x.id);
                    table.CheckConstraint("ck_webhook_endpoints_timeout", "timeout_seconds BETWEEN 1 AND 120");
                    table.CheckConstraint("ck_webhook_endpoints_url_scheme", "url LIKE 'http://%' OR url LIKE 'https://%'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_active_created",
                table: "webhook_endpoints",
                columns: new[] { "is_active", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_endpoints");
        }
    }
}
