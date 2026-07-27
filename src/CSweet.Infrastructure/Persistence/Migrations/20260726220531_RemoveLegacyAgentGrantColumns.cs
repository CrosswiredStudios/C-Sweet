using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyAgentGrantColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CapabilitiesJson",
                table: "AgentInstallationGrants");

            migrationBuilder.DropColumn(
                name: "PermissionsJson",
                table: "AgentInstallationGrants");

            migrationBuilder.DropColumn(
                name: "PublicationsJson",
                table: "AgentInstallationGrants");

            migrationBuilder.DropColumn(
                name: "RequestedCapabilitiesJson",
                table: "AgentInstallationGrants");

            migrationBuilder.DropColumn(
                name: "SubscriptionsJson",
                table: "AgentInstallationGrants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CapabilitiesJson",
                table: "AgentInstallationGrants",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PermissionsJson",
                table: "AgentInstallationGrants",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublicationsJson",
                table: "AgentInstallationGrants",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestedCapabilitiesJson",
                table: "AgentInstallationGrants",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionsJson",
                table: "AgentInstallationGrants",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
