using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrackLocalOfficeSetupLaunch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AdministratorApprovalRequestedAt",
                table: "LocalOfficeSetupSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalOfficeSetupSessions_CreatedByUserId",
                table: "LocalOfficeSetupSessions",
                column: "CreatedByUserId",
                unique: true,
                filter: "\"Status\" IN ('Created', 'Redeemed', 'Connected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LocalOfficeSetupSessions_CreatedByUserId",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "AdministratorApprovalRequestedAt",
                table: "LocalOfficeSetupSessions");
        }
    }
}
