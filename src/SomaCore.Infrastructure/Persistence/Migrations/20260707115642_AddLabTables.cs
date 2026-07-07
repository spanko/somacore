using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_uploads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    collected_at = table.Column<DateOnly>(type: "date", nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    file_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    file_size = table.Column<int>(type: "integer", nullable: false),
                    parse_status = table.Column<string>(type: "text", nullable: false),
                    parse_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    parsed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lab_uploads", x => x.id);
                    table.CheckConstraint("chk_lab_uploads_file_size", "file_size > 0 AND file_size <= 10485760");
                    table.CheckConstraint("chk_lab_uploads_parse_status", "parse_status IN ('parsed', 'failed', 'confirmed')");
                    table.ForeignKey(
                        name: "fk_lab_uploads_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lab_biomarkers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lab_upload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    biomarker_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    numeric_value = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    string_value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    reference_low = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    reference_high = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    reference_string = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    collected_at = table.Column<DateOnly>(type: "date", nullable: false),
                    flagged = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lab_biomarkers", x => x.id);
                    table.CheckConstraint("chk_lab_biomarkers_flagged", "flagged IN ('in_range', 'low', 'high', 'unknown')");
                    table.ForeignKey(
                        name: "fk_lab_biomarkers_lab_uploads_lab_upload_id",
                        column: x => x.lab_upload_id,
                        principalTable: "lab_uploads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_lab_biomarkers_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_lab_biomarkers_user_marker",
                table: "lab_biomarkers",
                columns: new[] { "user_id", "biomarker_name", "collected_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_lab_biomarkers_lab_upload_id",
                table: "lab_biomarkers",
                column: "lab_upload_id");

            migrationBuilder.CreateIndex(
                name: "idx_lab_uploads_user_collected",
                table: "lab_uploads",
                columns: new[] { "user_id", "source", "collected_at" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_biomarkers");

            migrationBuilder.DropTable(
                name: "lab_uploads");
        }
    }
}
