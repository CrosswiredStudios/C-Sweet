using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedRepositoryDefaultTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultTeamId",
                table: "RepositoryProvisioningPolicies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryProvisioningPolicies_OrganizationId_DefaultTeamId",
                table: "RepositoryProvisioningPolicies",
                columns: new[] { "OrganizationId", "DefaultTeamId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RepositoryProvisioningPolicies_OrganizationId_DefaultTeamId",
                table: "RepositoryProvisioningPolicies");

            migrationBuilder.DropColumn(
                name: "DefaultTeamId",
                table: "RepositoryProvisioningPolicies");
        }
    }
}
