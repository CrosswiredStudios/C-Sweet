using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfficeUpdateRecoveryAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecoveryError",
                table: "OfficeUpdateJob",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUpdateCheckAt",
                table: "LocalOfficeSetupSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaintenanceInterruptionCount",
                table: "DeliveryBuilds",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecoveryError",
                table: "OfficeUpdateJob");

            migrationBuilder.DropColumn(
                name: "LastUpdateCheckAt",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "MaintenanceInterruptionCount",
                table: "DeliveryBuilds");
        }
    }
}
