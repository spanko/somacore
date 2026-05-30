using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWhoopSleepsAndWorkouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "whoop_sleeps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    whoop_sleep_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    timezone_offset = table.Column<string>(type: "text", nullable: false),
                    nap = table.Column<bool>(type: "boolean", nullable: false),
                    score_state = table.Column<string>(type: "text", nullable: false),
                    sleep_performance_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    sleep_efficiency_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    sleep_consistency_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    total_in_bed_time_milli = table.Column<long>(type: "bigint", nullable: true),
                    total_sleep_time_milli = table.Column<long>(type: "bigint", nullable: true),
                    score = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ingested_via = table.Column<string>(type: "text", nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    raw_payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whoop_sleeps", x => x.id);
                    table.CheckConstraint("chk_whoop_sleeps_cons_range", "sleep_consistency_percentage IS NULL OR (sleep_consistency_percentage BETWEEN 0 AND 100)");
                    table.CheckConstraint("chk_whoop_sleeps_eff_range", "sleep_efficiency_percentage IS NULL OR (sleep_efficiency_percentage BETWEEN 0 AND 100)");
                    table.CheckConstraint("chk_whoop_sleeps_ingested_via", "ingested_via IN ('webhook', 'poller', 'on_open_pull')");
                    table.CheckConstraint("chk_whoop_sleeps_perf_range", "sleep_performance_percentage IS NULL OR (sleep_performance_percentage BETWEEN 0 AND 100)");
                    table.CheckConstraint("chk_whoop_sleeps_score_state", "score_state IN ('SCORED', 'PENDING_SCORE', 'UNSCORABLE')");
                    table.ForeignKey(
                        name: "fk_whoop_sleeps_external_connections_external_connection_id",
                        column: x => x.external_connection_id,
                        principalTable: "external_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_whoop_sleeps_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "whoop_workouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    whoop_workout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    timezone_offset = table.Column<string>(type: "text", nullable: false),
                    sport_name = table.Column<string>(type: "text", nullable: false),
                    score_state = table.Column<string>(type: "text", nullable: false),
                    strain = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: true),
                    average_heart_rate = table.Column<int>(type: "integer", nullable: true),
                    max_heart_rate = table.Column<int>(type: "integer", nullable: true),
                    kilojoule = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    score = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ingested_via = table.Column<string>(type: "text", nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    raw_payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whoop_workouts", x => x.id);
                    table.CheckConstraint("chk_whoop_workouts_avg_hr_range", "average_heart_rate IS NULL OR (average_heart_rate BETWEEN 0 AND 300)");
                    table.CheckConstraint("chk_whoop_workouts_ingested_via", "ingested_via IN ('webhook', 'poller', 'on_open_pull')");
                    table.CheckConstraint("chk_whoop_workouts_max_hr_range", "max_heart_rate IS NULL OR (max_heart_rate BETWEEN 0 AND 300)");
                    table.CheckConstraint("chk_whoop_workouts_score_state", "score_state IN ('SCORED', 'PENDING_SCORE', 'UNSCORABLE')");
                    table.CheckConstraint("chk_whoop_workouts_strain_range", "strain IS NULL OR (strain BETWEEN 0 AND 21)");
                    table.ForeignKey(
                        name: "fk_whoop_workouts_external_connections_external_connection_id",
                        column: x => x.external_connection_id,
                        principalTable: "external_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_whoop_workouts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_whoop_sleeps_connection_sleep",
                table: "whoop_sleeps",
                columns: new[] { "external_connection_id", "whoop_sleep_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_whoop_sleeps_user_start",
                table: "whoop_sleeps",
                columns: new[] { "user_id", "start_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_whoop_workouts_connection_workout",
                table: "whoop_workouts",
                columns: new[] { "external_connection_id", "whoop_workout_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_whoop_workouts_user_start",
                table: "whoop_workouts",
                columns: new[] { "user_id", "start_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whoop_sleeps");

            migrationBuilder.DropTable(
                name: "whoop_workouts");
        }
    }
}
