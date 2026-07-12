using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStravaActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "strava_activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    strava_activity_id = table.Column<long>(type: "bigint", nullable: false),
                    strava_athlete_id = table.Column<long>(type: "bigint", nullable: false),
                    activity_type = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    elapsed_seconds = table.Column<int>(type: "integer", nullable: false),
                    moving_seconds = table.Column<int>(type: "integer", nullable: true),
                    distance_meters = table.Column<decimal>(type: "numeric", nullable: true),
                    total_elevation_gain_m = table.Column<decimal>(type: "numeric", nullable: true),
                    average_speed_mps = table.Column<decimal>(type: "numeric", nullable: true),
                    max_speed_mps = table.Column<decimal>(type: "numeric", nullable: true),
                    average_hr = table.Column<int>(type: "integer", nullable: true),
                    max_hr = table.Column<int>(type: "integer", nullable: true),
                    average_cadence = table.Column<decimal>(type: "numeric", nullable: true),
                    average_watts = table.Column<decimal>(type: "numeric", nullable: true),
                    max_watts = table.Column<int>(type: "integer", nullable: true),
                    weighted_avg_watts = table.Column<int>(type: "integer", nullable: true),
                    device_watts = table.Column<bool>(type: "boolean", nullable: true),
                    kudos_count = table.Column<int>(type: "integer", nullable: true),
                    calories = table.Column<decimal>(type: "numeric", nullable: true),
                    hr_zones = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    splits = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    laps = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    raw_summary_payload = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    raw_detail_payload = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    detail_fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ingested_via = table.Column<string>(type: "text", nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_strava_activities", x => x.id);
                    table.CheckConstraint("chk_strava_activities_avg_hr_range", "average_hr IS NULL OR (average_hr BETWEEN 0 AND 300)");
                    table.CheckConstraint("chk_strava_activities_elapsed_positive", "elapsed_seconds > 0");
                    table.CheckConstraint("chk_strava_activities_max_hr_range", "max_hr IS NULL OR (max_hr BETWEEN 0 AND 300)");
                    table.ForeignKey(
                        name: "fk_strava_activities_external_connections_external_connection_",
                        column: x => x.external_connection_id,
                        principalTable: "external_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_strava_activities_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_strava_activities_activity_id",
                table: "strava_activities",
                column: "strava_activity_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_strava_activities_user_started",
                table: "strava_activities",
                columns: new[] { "user_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_strava_activities_external_connection_id",
                table: "strava_activities",
                column: "external_connection_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "strava_activities");
        }
    }
}
