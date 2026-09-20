using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrebuiltReleaseProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReleaseAssetName",
                table: "AgentPackageVersions",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseAssetUrl",
                table: "AgentPackageVersions",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseBundleDigest",
                table: "AgentPackageVersions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseTag",
                table: "AgentPackageVersions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReleaseAssetName",
                table: "AgentPackageVersions");

            migrationBuilder.DropColumn(
                name: "ReleaseAssetUrl",
                table: "AgentPackageVersions");

            migrationBuilder.DropColumn(
                name: "ReleaseBundleDigest",
                table: "AgentPackageVersions");

            migrationBuilder.DropColumn(
                name: "ReleaseTag",
                table: "AgentPackageVersions");
        }
    }
}
