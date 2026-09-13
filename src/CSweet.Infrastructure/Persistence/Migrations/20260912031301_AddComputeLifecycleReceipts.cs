using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComputeLifecycleReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "ComputeOperations",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestDigest",
                table: "ComputeOperations",
                type: "character varying(71)",
                maxLength: 71,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComputeOperations_OrganizationId_InstallationId_Idempotency~",
                table: "ComputeOperations",
                columns: new[] { "OrganizationId", "InstallationId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComputeOperations_OrganizationId_InstallationId_Idempotency~",
                table: "ComputeOperations");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "ComputeOperations");

            migrationBuilder.DropColumn(
                name: "RequestDigest",
                table: "ComputeOperations");
        }
    }
}
