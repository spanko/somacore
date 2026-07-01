using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SomaCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SetNullOnRecoveryConnectionFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_whoop_recoveries_external_connections_external_connection_id",
                table: "whoop_recoveries");

            migrationBuilder.AlterColumn<Guid>(
                name: "external_connection_id",
                table: "whoop_recoveries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddForeignKey(
                name: "fk_whoop_recoveries_external_connections_external_connection_id",
                table: "whoop_recoveries",
                column: "external_connection_id",
                principalTable: "external_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_whoop_recoveries_external_connections_external_connection_id",
                table: "whoop_recoveries");

            migrationBuilder.AlterColumn<Guid>(
                name: "external_connection_id",
                table: "whoop_recoveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_whoop_recoveries_external_connections_external_connection_id",
                table: "whoop_recoveries",
                column: "external_connection_id",
                principalTable: "external_connections",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
