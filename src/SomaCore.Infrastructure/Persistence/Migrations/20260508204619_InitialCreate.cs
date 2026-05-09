using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,");

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entra_oid = table.Column<Guid>(type: "uuid", nullable: false),
                    entra_tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "external_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    key_vault_secret_name = table.Column<string>(type: "text", nullable: false),
                    last_refresh_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_refresh_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refresh_failure_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_refresh_error = table.Column<string>(type: "text", nullable: true),
                    connection_metadata = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_connections", x => x.id);
                    table.CheckConstraint("chk_external_connections_kv_secret_name_not_empty", "length(key_vault_secret_name) > 0");
                    table.CheckConstraint("chk_external_connections_source", "source IN ('whoop', 'oura', 'strava', 'apple_health', 'manual')");
                    table.CheckConstraint("chk_external_connections_status", "status IN ('active', 'revoked', 'refresh_failed', 'pending_authorization')");
                    table.ForeignKey(
                        name: "fk_external_connections_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    http_status_code = table.Column<int>(type: "integer", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    context = table.Column<JsonDocument>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_audit", x => x.id);
                    table.CheckConstraint("chk_oauth_audit_action", "action IN ('authorize', 'callback_success', 'callback_failed', 'token_refresh_success', 'token_refresh_failed', 'revoke_detected', 'manual_disconnect')");
                    table.CheckConstraint("chk_oauth_audit_source", "source IN ('whoop', 'oura', 'strava', 'apple_health')");
                    table.ForeignKey(
                        name: "fk_oauth_audit_external_connections_external_connection_id",
                        column: x => x.external_connection_id,
                        principalTable: "external_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_oauth_audit_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "webhook_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_event_id = table.Column<string>(type: "text", nullable: false),
                    source_trace_id = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "received"),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    processing_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processing_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    raw_body = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    signature_header = table.Column<string>(type: "text", nullable: false),
                    signature_timestamp_header = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_events", x => x.id);
                    table.CheckConstraint("chk_webhook_events_source", "source IN ('whoop', 'oura', 'strava')");
                    table.CheckConstraint("chk_webhook_events_status", "status IN ('received', 'processing', 'processed', 'failed', 'discarded')");
                    table.ForeignKey(
                        name: "fk_webhook_events_external_connections_external_connection_id",
                        column: x => x.external_connection_id,
                        principalTable: "external_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_webhook_events_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "whoop_recoveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    whoop_cycle_id = table.Column<long>(type: "bigint", nullable: false),
                    whoop_sleep_id = table.Column<Guid>(type: "uuid", nullable: true),
                    score_state = table.Column<string>(type: "text", nullable: false),
                    recovery_score = table.Column<int>(type: "integer", nullable: true),
                    hrv_rmssd_milli = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    resting_heart_rate = table.Column<int>(type: "integer", nullable: true),
                    spo2_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    skin_temp_celsius = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    cycle_start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cycle_end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ingested_via = table.Column<string>(type: "text", nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    raw_payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whoop_recoveries", x => x.id);
                    table.CheckConstraint("chk_whoop_recoveries_ingested_via", "ingested_via IN ('webhook', 'poller', 'on_open_pull')");
                    table.CheckConstraint("chk_whoop_recoveries_score_range", "recovery_score IS NULL OR (recovery_score BETWEEN 0 AND 100)");
                    table.CheckConstraint("chk_whoop_recoveries_score_state", "score_state IN ('SCORED', 'PENDING_SCORE', 'UNSCORABLE')");
                    table.CheckConstraint("chk_whoop_recoveries_scored_has_score", "(score_state = 'SCORED' AND recovery_score IS NOT NULL) OR (score_state IN ('PENDING_SCORE', 'UNSCORABLE'))");
                    table.CheckConstraint("chk_whoop_recoveries_spo2_range", "spo2_percentage IS NULL OR (spo2_percentage BETWEEN 0 AND 100)");
                    table.ForeignKey(
                        name: "fk_whoop_recoveries_external_connections_external_connection_id",
                        column: x => x.external_connection_id,
                        principalTable: "external_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_whoop_recoveries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_external_connections_next_refresh",
                table: "external_connections",
                column: "next_refresh_at",
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "idx_external_connections_user_id",
                table: "external_connections",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_external_connections_user_source_active",
                table: "external_connections",
                columns: new[] { "user_id", "source" },
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "idx_oauth_audit_connection_occurred",
                table: "oauth_audit",
                columns: new[] { "external_connection_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "external_connection_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_oauth_audit_user_occurred",
                table: "oauth_audit",
                columns: new[] { "user_id", "occurred_at" },
                descending: new[] { false, true },
                filter: "user_id IS NOT NULL");

            // Hand-edit: EF Core has no fluent API for expression-based index columns.
            // The schema spec calls for an index on lower(email); we emit raw SQL.
            migrationBuilder.Sql("CREATE INDEX idx_users_email ON users (lower(email));");

            migrationBuilder.CreateIndex(
                name: "ix_users_entra_oid",
                table: "users",
                column: "entra_oid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_webhook_events_dedupe",
                table: "webhook_events",
                columns: new[] { "source", "source_event_id", "source_trace_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_webhook_events_pending",
                table: "webhook_events",
                column: "received_at",
                filter: "status IN ('received', 'processing')");

            migrationBuilder.CreateIndex(
                name: "idx_webhook_events_user_received",
                table: "webhook_events",
                columns: new[] { "user_id", "received_at" },
                descending: new[] { false, true },
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_events_external_connection_id",
                table: "webhook_events",
                column: "external_connection_id");

            migrationBuilder.CreateIndex(
                name: "idx_whoop_recoveries_connection_cycle",
                table: "whoop_recoveries",
                columns: new[] { "external_connection_id", "whoop_cycle_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_whoop_recoveries_sleep_id",
                table: "whoop_recoveries",
                column: "whoop_sleep_id",
                filter: "whoop_sleep_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_whoop_recoveries_user_cycle_start",
                table: "whoop_recoveries",
                columns: new[] { "user_id", "cycle_start_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oauth_audit");

            migrationBuilder.DropTable(
                name: "webhook_events");

            migrationBuilder.DropTable(
                name: "whoop_recoveries");

            migrationBuilder.DropTable(
                name: "external_connections");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
