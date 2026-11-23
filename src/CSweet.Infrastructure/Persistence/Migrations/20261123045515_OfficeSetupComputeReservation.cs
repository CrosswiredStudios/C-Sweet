using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfficeSetupComputeReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(name: "FirstOfficeBusinessId", table: "SystemConfigurations", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(name: "FirstOfficeSetupUserId", table: "SystemConfigurations", type: "uuid", nullable: true);
            migrationBuilder.AddColumn<Guid>(
                name: "ComputeReservationBusinessId",
                table: "LocalOfficeSetupSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ComputeReservationImageDigest",
                table: "LocalOfficeSetupSessions",
                type: "character varying(71)",
                maxLength: 71,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "FirstOfficeBusinessId", table: "SystemConfigurations");
            migrationBuilder.DropColumn(name: "FirstOfficeSetupUserId", table: "SystemConfigurations");
            migrationBuilder.DropColumn(
                name: "ComputeReservationBusinessId",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "ComputeReservationImageDigest",
                table: "LocalOfficeSetupSessions");
        }
    }
}
