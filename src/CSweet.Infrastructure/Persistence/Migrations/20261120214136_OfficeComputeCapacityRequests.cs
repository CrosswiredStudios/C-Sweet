using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfficeComputeCapacityRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OfficeComputeCapacityRequests",
                columns: table => new
                {
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeComputeCapacityRequests", x => x.InstallationId);
                    table.ForeignKey(
                        name: "FK_OfficeComputeCapacityRequests_AgentInstallations_Installati~",
                        column: x => x.InstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OfficeComputeCapacityRequests_CoreOrganizations_Organizatio~",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfficeComputeCapacityRequests_OrganizationId_RequestedAt_In~",
                table: "OfficeComputeCapacityRequests",
                columns: new[] { "OrganizationId", "RequestedAt", "InstallationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfficeComputeCapacityRequests");
        }
    }
}
