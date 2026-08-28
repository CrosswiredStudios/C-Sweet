using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactPackageIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "ArtifactPackages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LastDecisionIdempotencyKey",
                table: "ArtifactPackages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSubmissionIdempotencyKey",
                table: "ArtifactPackages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactPackages_OrganizationId_IdempotencyKey",
                table: "ArtifactPackages",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArtifactPackages_OrganizationId_IdempotencyKey",
                table: "ArtifactPackages");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "ArtifactPackages");

            migrationBuilder.DropColumn(
                name: "LastDecisionIdempotencyKey",
                table: "ArtifactPackages");

            migrationBuilder.DropColumn(
                name: "LastSubmissionIdempotencyKey",
                table: "ArtifactPackages");
        }
    }
}
