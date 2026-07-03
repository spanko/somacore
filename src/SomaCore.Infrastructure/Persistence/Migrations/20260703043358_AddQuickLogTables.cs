using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuickLogTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "agent_invocations",
                type: "text",
                nullable: false,
                defaultValue: "daily_card");

            migrationBuilder.CreateTable(
                name: "healthkit_workouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_bundle_id = table.Column<string>(type: "text", nullable: false),
                    hk_sample_uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    workout_type = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    elapsed_seconds = table.Column<int>(type: "integer", nullable: false),
                    total_energy_kcal = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    total_distance_m = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    average_hr = table.Column<int>(type: "integer", nullable: true),
                    hk_metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_healthkit_workouts", x => x.id);
                    table.CheckConstraint("chk_healthkit_workouts_avg_hr_range", "average_hr IS NULL OR (average_hr BETWEEN 0 AND 300)");
                    table.CheckConstraint("chk_healthkit_workouts_elapsed_positive", "elapsed_seconds > 0");
                    table.ForeignKey(
                        name: "fk_healthkit_workouts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mfp_food_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    meal_date = table.Column<DateOnly>(type: "date", nullable: false),
                    meal_slot = table.Column<string>(type: "text", nullable: false),
                    logged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    calories = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    protein_g = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    carbs_g = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    fat_g = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    fiber_g = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    sugar_g = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    sodium_mg = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: true),
                    food_items = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    raw_payload = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ingested_via = table.Column<string>(type: "text", nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfp_food_entries", x => x.id);
                    table.CheckConstraint("chk_mfp_food_entries_calories_range", "calories IS NULL OR (calories BETWEEN 0 AND 20000)");
                    table.CheckConstraint("chk_mfp_food_entries_meal_slot", "meal_slot IN ('breakfast', 'lunch', 'dinner', 'snack', 'other')");
                    table.CheckConstraint("chk_mfp_food_entries_source", "source IN ('manual', 'healthkit_ios', 'csv_upload')");
                    table.ForeignKey(
                        name: "fk_mfp_food_entries_external_connections_external_connection_id",
                        column: x => x.external_connection_id,
                        principalTable: "external_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_mfp_food_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    active_until = table.Column<DateOnly>(type: "date", nullable: true),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_notes", x => x.id);
                    table.CheckConstraint("chk_user_notes_category", "category IS NULL OR category IN ('symptom', 'schedule', 'context')");
                    table.CheckConstraint("chk_user_notes_source", "source IN ('quick_log', 'conversation')");
                    table.ForeignKey(
                        name: "fk_user_notes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_healthkit_workouts_sample_uuid",
                table: "healthkit_workouts",
                column: "hk_sample_uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_healthkit_workouts_user_started",
                table: "healthkit_workouts",
                columns: new[] { "user_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_mfp_food_entries_user_date",
                table: "mfp_food_entries",
                columns: new[] { "user_id", "meal_date" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_mfp_food_entries_user_date_slot_source",
                table: "mfp_food_entries",
                columns: new[] { "user_id", "meal_date", "meal_slot", "source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mfp_food_entries_external_connection_id",
                table: "mfp_food_entries",
                column: "external_connection_id");

            migrationBuilder.CreateIndex(
                name: "idx_user_notes_user_active_until",
                table: "user_notes",
                columns: new[] { "user_id", "active_until" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "healthkit_workouts");

            migrationBuilder.DropTable(
                name: "mfp_food_entries");

            migrationBuilder.DropTable(
                name: "user_notes");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "agent_invocations");
        }
    }
}
