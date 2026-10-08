using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatPromptMetadataEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE "AgentRuntimeInstances" IN SHARE ROW EXCLUSIVE MODE;
                UPDATE "AgentRuntimeInstances" SET "MemoryReadEvidenceVersion"=0 WHERE "MemoryReadEvidenceVersion"<3;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "AgentRuntimeInstances" WHERE "MemoryReadEvidenceVersion">=3) OR
                       EXISTS(SELECT 1 FROM "AgentWorkItems" WHERE "MemoryRecallReceiptJson"::jsonb #>> '{prompt,version}'='2') OR
                       EXISTS(SELECT 1 FROM "AgentMemoryReadReceipts" WHERE "Capability"='platform.memory.queued-recall.v1'
                            AND "EvidenceJson"::jsonb #>> '{prompt,version}'='2') THEN
                        RAISE EXCEPTION 'memory_prompt_metadata_downgrade_requires_snapshot';
                    END IF;
                END $$;
                """);
        }
    }
}
