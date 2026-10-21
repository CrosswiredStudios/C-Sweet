using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoleScopedManualHiring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ManualSelectionRequestedAt",
                table: "WorkforcePlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ManualSelectionRequestedByOrganizationUserId",
                table: "WorkforcePlans",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManualSelectionRequestedAt",
                table: "WorkforcePlans");

            migrationBuilder.DropColumn(
                name: "ManualSelectionRequestedByOrganizationUserId",
                table: "WorkforcePlans");
        }
    }
}
