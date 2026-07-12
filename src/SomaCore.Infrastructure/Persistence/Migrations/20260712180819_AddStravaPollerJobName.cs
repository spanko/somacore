using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStravaPollerJobName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_job_runs_job_name",
                table: "job_runs");

            migrationBuilder.AddCheckConstraint(
                name: "chk_job_runs_job_name",
                table: "job_runs",
                sql: "job_name IN ('reconciliation-poller', 'token-refresh-sweeper', 'strava-reconciliation-poller')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_job_runs_job_name",
                table: "job_runs");

            migrationBuilder.AddCheckConstraint(
                name: "chk_job_runs_job_name",
                table: "job_runs",
                sql: "job_name IN ('reconciliation-poller', 'token-refresh-sweeper')");
        }
    }
}
