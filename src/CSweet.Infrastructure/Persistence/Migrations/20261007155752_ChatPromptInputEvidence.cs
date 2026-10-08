using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatPromptInputEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Prior runtimes may have received direct conversation text even when
            // no recall root was selected, and those deliveries were not recorded.
            migrationBuilder.Sql("""
                LOCK TABLE "AgentRuntimeInstances" IN SHARE ROW EXCLUSIVE MODE;
                UPDATE "AgentRuntimeInstances" SET "MemoryReadEvidenceVersion"=0 WHERE "MemoryReadEvidenceVersion"<2;
                """);
            migrationBuilder.Sql(InstallTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "AgentRuntimeInstances" WHERE "MemoryReadEvidenceVersion">=2) OR
                       EXISTS(SELECT 1 FROM "AgentWorkItems" WHERE "MemoryRecallReceiptJson"::jsonb->>'version'='2') OR
                       EXISTS(SELECT 1 FROM "AgentMemoryReadReceipts" WHERE "Capability"='platform.memory.queued-recall.v1'
                            AND "EvidenceJson"::jsonb->>'version'='2') THEN
                        RAISE EXCEPTION 'memory_prompt_evidence_downgrade_requires_snapshot';
                    END IF;
                END $$;
                DROP TRIGGER csweet_queued_prompt_immutable ON "AgentWorkItems";
                DROP TRIGGER csweet_delivered_prompt_immutable ON "AgentMemoryReadReceipts";
                DROP FUNCTION csweet_queued_prompt_immutable();
                DROP FUNCTION csweet_delivered_prompt_immutable();
                """);
        }

        internal const string InstallTriggers = """
            CREATE FUNCTION csweet_queued_prompt_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."MemoryRecallReceiptJson" IS NOT NULL AND
                    (NEW."MemoryRecallReceiptJson" IS DISTINCT FROM OLD."MemoryRecallReceiptJson" OR
                     NEW."OrganizationId" IS DISTINCT FROM OLD."OrganizationId" OR
                     NEW."AgentInstallationId" IS DISTINCT FROM OLD."AgentInstallationId" OR
                     NEW."PayloadHash" IS DISTINCT FROM OLD."PayloadHash") THEN
                    RAISE EXCEPTION 'memory_prompt_evidence_immutable';
                END IF;
                IF OLD."MemoryRecallReceiptJson" IS NOT NULL AND
                    (NEW."ProtectedPayload" IS DISTINCT FROM OLD."ProtectedPayload" OR
                     NEW."SourceType" IS DISTINCT FROM OLD."SourceType" OR NEW."SourceId" IS DISTINCT FROM OLD."SourceId") AND
                    NOT (OLD."MemoryErasedAt" IS NULL AND NEW."MemoryErasedAt" IS NOT NULL AND NEW."Name"='memory.erased' AND
                         NEW."SourceType" IS NULL AND NEW."SourceId" IS NULL) THEN
                    RAISE EXCEPTION 'memory_prompt_evidence_immutable';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_queued_prompt_immutable BEFORE UPDATE ON "AgentWorkItems"
                FOR EACH ROW EXECUTE FUNCTION csweet_queued_prompt_immutable();

            CREATE FUNCTION csweet_delivered_prompt_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP='UPDATE' OR EXISTS(SELECT 1 FROM "AgentRuntimeInstances" WHERE "Id"=OLD."RuntimeId") THEN
                    RAISE EXCEPTION 'memory_prompt_evidence_immutable';
                END IF;
                RETURN OLD;
            END $$;
            CREATE TRIGGER csweet_delivered_prompt_immutable BEFORE UPDATE OR DELETE ON "AgentMemoryReadReceipts"
                FOR EACH ROW EXECUTE FUNCTION csweet_delivered_prompt_immutable();
            """;
    }
}
