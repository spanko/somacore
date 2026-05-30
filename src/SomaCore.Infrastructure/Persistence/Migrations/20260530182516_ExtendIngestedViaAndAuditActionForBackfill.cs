using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendIngestedViaAndAuditActionForBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_whoop_workouts_ingested_via",
                table: "whoop_workouts");

            migrationBuilder.DropCheckConstraint(
                name: "chk_whoop_sleeps_ingested_via",
                table: "whoop_sleeps");

            migrationBuilder.DropCheckConstraint(
                name: "chk_whoop_recoveries_ingested_via",
                table: "whoop_recoveries");

            migrationBuilder.DropCheckConstraint(
                name: "chk_oauth_audit_action",
                table: "oauth_audit");

            migrationBuilder.AddCheckConstraint(
                name: "chk_whoop_workouts_ingested_via",
                table: "whoop_workouts",
                sql: "ingested_via IN ('webhook', 'poller', 'on_open_pull', 'backfill')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_whoop_sleeps_ingested_via",
                table: "whoop_sleeps",
                sql: "ingested_via IN ('webhook', 'poller', 'on_open_pull', 'backfill')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_whoop_recoveries_ingested_via",
                table: "whoop_recoveries",
                sql: "ingested_via IN ('webhook', 'poller', 'on_open_pull', 'backfill')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_oauth_audit_action",
                table: "oauth_audit",
                sql: "action IN ('authorize', 'callback_success', 'callback_failed', 'token_refresh_success', 'token_refresh_failed', 'revoke_detected', 'manual_disconnect', 'backfill')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_whoop_workouts_ingested_via",
                table: "whoop_workouts");

            migrationBuilder.DropCheckConstraint(
                name: "chk_whoop_sleeps_ingested_via",
                table: "whoop_sleeps");

            migrationBuilder.DropCheckConstraint(
                name: "chk_whoop_recoveries_ingested_via",
                table: "whoop_recoveries");

            migrationBuilder.DropCheckConstraint(
                name: "chk_oauth_audit_action",
                table: "oauth_audit");

            migrationBuilder.AddCheckConstraint(
                name: "chk_whoop_workouts_ingested_via",
                table: "whoop_workouts",
                sql: "ingested_via IN ('webhook', 'poller', 'on_open_pull')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_whoop_sleeps_ingested_via",
                table: "whoop_sleeps",
                sql: "ingested_via IN ('webhook', 'poller', 'on_open_pull')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_whoop_recoveries_ingested_via",
                table: "whoop_recoveries",
                sql: "ingested_via IN ('webhook', 'poller', 'on_open_pull')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_oauth_audit_action",
                table: "oauth_audit",
                sql: "action IN ('authorize', 'callback_success', 'callback_failed', 'token_refresh_success', 'token_refresh_failed', 'revoke_detected', 'manual_disconnect')");
        }
    }
}
