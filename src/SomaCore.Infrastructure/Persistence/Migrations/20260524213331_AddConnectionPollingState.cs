using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionPollingState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_poll_outcome",
                table: "external_connections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_polled_at",
                table: "external_connections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "chk_external_connections_last_poll_outcome",
                table: "external_connections",
                sql: "last_poll_outcome IS NULL OR last_poll_outcome IN ('Skipped', 'Polled', 'Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_external_connections_last_poll_outcome",
                table: "external_connections");

            migrationBuilder.DropColumn(
                name: "last_poll_outcome",
                table: "external_connections");

            migrationBuilder.DropColumn(
                name: "last_polled_at",
                table: "external_connections");
        }
    }
}
