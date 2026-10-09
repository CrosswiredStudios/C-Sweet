using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopedMemoryAudienceAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MemoryAudienceRevision",
                table: "CoreConversations",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
            migrationBuilder.Sql(AuthorityTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "CoreConversations" WHERE "MemoryAudienceRevision">1)
                        OR EXISTS (SELECT 1 FROM "AgentMemoryReadReceipts"
                            WHERE "EvidenceJson" LIKE '%"scopedAuthorityHash"%') THEN
                        RAISE EXCEPTION 'Scoped memory authority has been used; downgrading would discard revocation history.';
                    END IF;
                END $$;
                DROP TRIGGER IF EXISTS csweet_memory_participant_authority ON "ConversationParticipants";
                DROP TRIGGER IF EXISTS csweet_memory_conversation_authority ON "CoreConversations";
                DROP FUNCTION IF EXISTS csweet_memory_participant_authority();
                DROP FUNCTION IF EXISTS csweet_memory_conversation_authority();
                """);
            migrationBuilder.DropColumn(
                name: "MemoryAudienceRevision",
                table: "CoreConversations");
        }

        // Read positions and message arrivals never invalidate an audience certificate.
        // Membership changes and archive/merge transitions do, including change-and-restore.
        public const string AuthorityTriggers = """
            CREATE OR REPLACE FUNCTION csweet_memory_conversation_authority() RETURNS trigger AS $$
            BEGIN
                IF ROW(NEW."OrganizationId",NEW."Kind",NEW."AgentOrganizationUserId",NEW."InitiatedByOrganizationUserId",
                    NEW."TeamId",NEW."WorkstreamId",NEW."IsPrivate",NEW."ArchivedAt",NEW."MergedIntoConversationId")
                    IS DISTINCT FROM
                    ROW(OLD."OrganizationId",OLD."Kind",OLD."AgentOrganizationUserId",OLD."InitiatedByOrganizationUserId",
                    OLD."TeamId",OLD."WorkstreamId",OLD."IsPrivate",OLD."ArchivedAt",OLD."MergedIntoConversationId")
                    OR NEW."MemoryAudienceRevision" IS DISTINCT FROM OLD."MemoryAudienceRevision" THEN
                    NEW."MemoryAudienceRevision" := OLD."MemoryAudienceRevision" + 1;
                ELSE NEW."MemoryAudienceRevision" := OLD."MemoryAudienceRevision";
                END IF;
                RETURN NEW;
            END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER csweet_memory_conversation_authority BEFORE UPDATE ON "CoreConversations"
                FOR EACH ROW EXECUTE FUNCTION csweet_memory_conversation_authority();

            CREATE OR REPLACE FUNCTION csweet_memory_participant_authority() RETURNS trigger AS $$
            BEGIN
                IF TG_OP='UPDATE' AND ROW(NEW."ConversationId",NEW."OrganizationUserId",NEW."Role",NEW."JoinedAt",NEW."LeftAt")
                    IS NOT DISTINCT FROM ROW(OLD."ConversationId",OLD."OrganizationUserId",OLD."Role",OLD."JoinedAt",OLD."LeftAt")
                    THEN RETURN NEW;
                END IF;
                IF TG_OP IN ('UPDATE','DELETE') THEN
                    UPDATE "CoreConversations" SET "MemoryAudienceRevision"="MemoryAudienceRevision"+1 WHERE "Id"=OLD."ConversationId";
                END IF;
                IF TG_OP='INSERT' OR (TG_OP='UPDATE' AND NEW."ConversationId" IS DISTINCT FROM OLD."ConversationId") THEN
                    UPDATE "CoreConversations" SET "MemoryAudienceRevision"="MemoryAudienceRevision"+1 WHERE "Id"=NEW."ConversationId";
                END IF;
                RETURN NULL;
            END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER csweet_memory_participant_authority AFTER INSERT OR UPDATE OR DELETE ON "ConversationParticipants"
                FOR EACH ROW EXECUTE FUNCTION csweet_memory_participant_authority();
            """;
    }
}
