using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthenticatedMemoryReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryReviewReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PreviousRevision = table.Column<long>(type: "bigint", nullable: false),
                    ResultClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryReviewReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryReviewReceipts_ClaimId_PreviousRevision",
                table: "MemoryReviewReceipts",
                columns: new[] { "ClaimId", "PreviousRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryReviewReceipts_OrganizationId_EmployeeId_CreatedAt",
                table: "MemoryReviewReceipts",
                columns: new[] { "OrganizationId", "EmployeeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryReviewReceipts_OrganizationId_OperationId",
                table: "MemoryReviewReceipts",
                columns: new[] { "OrganizationId", "OperationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryReviewReceipts");
        }
    }
}
