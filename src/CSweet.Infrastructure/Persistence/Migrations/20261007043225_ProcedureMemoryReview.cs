using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcedureMemoryReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MemoryReviewReceipts_ClaimId_PreviousRevision",
                table: "MemoryReviewReceipts");

            migrationBuilder.AddColumn<string>(
                name: "RecordKind",
                table: "MemoryReviewReceipts",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Claim");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryReviewReceipts_RecordKind_ClaimId_PreviousRevision",
                table: "MemoryReviewReceipts",
                columns: new[] { "RecordKind", "ClaimId", "PreviousRevision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Older binaries interpret every receipt as a claim. Never erase procedure audit identity.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "MemoryReviewReceipts" WHERE "RecordKind" <> 'Claim') THEN
                        RAISE EXCEPTION 'memory_review_downgrade_requires_snapshot';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropIndex(
                name: "IX_MemoryReviewReceipts_RecordKind_ClaimId_PreviousRevision",
                table: "MemoryReviewReceipts");

            migrationBuilder.DropColumn(
                name: "RecordKind",
                table: "MemoryReviewReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryReviewReceipts_ClaimId_PreviousRevision",
                table: "MemoryReviewReceipts",
                columns: new[] { "ClaimId", "PreviousRevision" },
                unique: true);
        }
    }
}
