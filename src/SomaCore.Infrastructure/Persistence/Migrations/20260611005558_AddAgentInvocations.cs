using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentInvocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_invocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_snapshot = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    todays_read = table.Column<string>(type: "text", nullable: false),
                    actions_json = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    model_id = table.Column<string>(type: "text", nullable: false),
                    cached_input_tokens = table.Column<int>(type: "integer", nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    cost_estimate_usd = table.Column<decimal>(type: "numeric(10,6)", precision: 10, scale: 6, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    trace_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_invocations", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_invocations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_agent_invocations_user_created",
                table: "agent_invocations",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_invocations");
        }
    }
}
