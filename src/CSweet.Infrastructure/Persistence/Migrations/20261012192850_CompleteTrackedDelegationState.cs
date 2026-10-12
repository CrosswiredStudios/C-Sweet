using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteTrackedDelegationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TrackedDelegations_OrganizationId_CoordinatorId_Idempotency~",
                table: "TrackedDelegations");

            migrationBuilder.AddColumn<Guid>(
                name: "AuthoredByOrganizationUserId",
                table: "TrackedDelegations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "InstructionAuthorId",
                table: "TrackedDelegations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSubstantiveAt",
                table: "TrackedDelegations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReconciledAt",
                table: "TrackedDelegations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "TrackedDelegations" AS d SET
                    "AuthoredByOrganizationUserId" = d."CoordinatorId",
                    "InstructionAuthorId" = COALESCE((SELECT m."SenderOrganizationUserId" FROM "CoreConversationMessages" m WHERE m."Id" = d."SourceMessageId"), d."OriginalRequesterId"),
                    "LastSubstantiveAt" = d."UpdatedAt";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_TrackedDelegations_OrganizationId_AuthoredByOrganizationUse~",
                table: "TrackedDelegations",
                columns: new[] { "OrganizationId", "AuthoredByOrganizationUserId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TrackedDelegations_OrganizationId_AuthoredByOrganizationUse~",
                table: "TrackedDelegations");

            migrationBuilder.DropColumn(
                name: "AuthoredByOrganizationUserId",
                table: "TrackedDelegations");

            migrationBuilder.DropColumn(
                name: "InstructionAuthorId",
                table: "TrackedDelegations");

            migrationBuilder.DropColumn(
                name: "LastSubstantiveAt",
                table: "TrackedDelegations");

            migrationBuilder.DropColumn(
                name: "ReconciledAt",
                table: "TrackedDelegations");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedDelegations_OrganizationId_CoordinatorId_Idempotency~",
                table: "TrackedDelegations",
                columns: new[] { "OrganizationId", "CoordinatorId", "IdempotencyKey" },
                unique: true);
        }
    }
}
