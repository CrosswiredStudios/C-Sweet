using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyAgentHostStorageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentPackageCachePath",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropColumn(
                name: "AgentSourceRootPath",
                table: "AgentRuntimeGlobalSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentPackageCachePath",
                table: "AgentRuntimeGlobalSettings",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AgentSourceRootPath",
                table: "AgentRuntimeGlobalSettings",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");
        }
    }
}
