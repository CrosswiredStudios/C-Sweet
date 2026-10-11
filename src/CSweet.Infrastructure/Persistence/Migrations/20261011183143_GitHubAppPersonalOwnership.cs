using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GitHubAppPersonalOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerType",
                table: "PlatformGitHubAppCredentials",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Organization");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerType",
                table: "PlatformGitHubAppCredentials");
        }
    }
}
