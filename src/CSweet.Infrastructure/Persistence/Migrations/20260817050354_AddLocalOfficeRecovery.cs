using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalOfficeRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LocalOfficeSetupSessions_CreatedByUserId",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.AddColumn<string>(
                name: "RecoveryAction",
                table: "LocalOfficeSetupSessions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<bool>(
                name: "RecoveryCanReconnect",
                table: "LocalOfficeSetupSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SetupReceiptHash",
                table: "LocalOfficeSetupSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalOfficeSetupSessions_CreatedByUserId",
                table: "LocalOfficeSetupSessions",
                column: "CreatedByUserId",
                unique: true,
                filter: "\"Status\" IN ('Created', 'Redeemed', 'Connected', 'RecoveryRequired', 'RemovalInProgress')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LocalOfficeSetupSessions_CreatedByUserId",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "RecoveryAction",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "RecoveryCanReconnect",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "SetupReceiptHash",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.CreateIndex(
                name: "IX_LocalOfficeSetupSessions_CreatedByUserId",
                table: "LocalOfficeSetupSessions",
                column: "CreatedByUserId",
                unique: true,
                filter: "\"Status\" IN ('Created', 'Redeemed', 'Connected')");
        }
    }
}
