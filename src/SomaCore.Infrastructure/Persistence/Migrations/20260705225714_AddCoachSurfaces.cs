using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoachSurfaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coach_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coach_threads", x => x.id);
                    table.CheckConstraint("chk_coach_threads_subject_type", "subject_type IN ('document', 'meal', 'workout', 'note', 'general')");
                    table.ForeignKey(
                        name: "fk_coach_threads_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    file_size = table.Column<int>(type: "integer", nullable: false),
                    parse_status = table.Column<string>(type: "text", nullable: false),
                    parse_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    extracted_text = table.Column<string>(type: "text", nullable: true),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_documents", x => x.id);
                    table.CheckConstraint("chk_user_documents_file_size", "file_size > 0 AND file_size <= 10485760");
                    table.CheckConstraint("chk_user_documents_parse_status", "parse_status IN ('parsed', 'failed')");
                    table.ForeignKey(
                        name: "fk_user_documents_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "coach_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    refusal = table.Column<bool>(type: "boolean", nullable: false),
                    invocation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coach_messages", x => x.id);
                    table.CheckConstraint("chk_coach_messages_role", "role IN ('user', 'coach')");
                    table.ForeignKey(
                        name: "fk_coach_messages_coach_threads_thread_id",
                        column: x => x.thread_id,
                        principalTable: "coach_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_coach_messages_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_coach_messages_thread_created",
                table: "coach_messages",
                columns: new[] { "thread_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_coach_messages_user_id",
                table: "coach_messages",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_coach_threads_user_last_message",
                table: "coach_threads",
                columns: new[] { "user_id", "last_message_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "idx_user_documents_user_uploaded",
                table: "user_documents",
                columns: new[] { "user_id", "uploaded_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coach_messages");

            migrationBuilder.DropTable(
                name: "user_documents");

            migrationBuilder.DropTable(
                name: "coach_threads");
        }
    }
}
