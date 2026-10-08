using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableMemoryCaptureExclusions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryCaptureExclusions",
                columns: table => new
                {
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ExcludedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryCaptureExclusions", x => x.SourceMessageId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCaptureExclusions_OrganizationId_EmployeeId_ExcludedAt",
                table: "MemoryCaptureExclusions",
                columns: new[] { "OrganizationId", "EmployeeId", "ExcludedAt" });

            // Preserve earlier permanent validation failures before operators prune old jobs.
            // This records the existing failure as evidence; it does not invent a human decision.
            migrationBuilder.Sql("""
                INSERT INTO "MemoryCaptureExclusions"
                    ("SourceMessageId", "OrganizationId", "EmployeeId", "TriggerJobId", "ReasonCode", "ExcludedAt")
                SELECT job."ConversationMessageId", conversation."OrganizationId", conversation."AgentOrganizationUserId",
                    job."Id", job."LastError", CURRENT_TIMESTAMP
                FROM "MemoryCaptureOutbox" job
                JOIN "CoreConversationMessages" message ON message."Id" = job."ConversationMessageId"
                JOIN "CoreConversations" conversation ON conversation."Id" = message."ConversationId"
                WHERE job."LastError" IN ('memory_enrichment_source_invalidated', 'memory_enrichment_unverifiable_output', 'memory_capture_excluded')
                    AND conversation."AgentOrganizationUserId" IS NOT NULL
                ON CONFLICT ("SourceMessageId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryCaptureExclusions");
        }
    }
}
