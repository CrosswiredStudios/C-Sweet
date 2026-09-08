using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

public partial class CanonicalDirectChats : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("DirectParticipantKey", "CoreConversations", type: "character varying(65)", maxLength: 65, nullable: true);
        migrationBuilder.AddColumn<Guid>("MergedIntoConversationId", "CoreConversations", type: "uuid", nullable: true);
        migrationBuilder.Sql("""
            LOCK TABLE "CoreConversations", "ConversationParticipants" IN SHARE ROW EXCLUSIVE MODE;
            UPDATE "CoreConversations" c SET "DirectParticipantKey" = pairs.key
            FROM (
                SELECT "ConversationId", string_agg(replace("OrganizationUserId"::text, '-', ''), ':' ORDER BY "OrganizationUserId") AS key
                FROM "ConversationParticipants" WHERE "LeftAt" IS NULL
                GROUP BY "ConversationId" HAVING count(*) = 2
            ) pairs WHERE c."Id" = pairs."ConversationId" AND c."Kind" = 'DirectHumanAgent';

            CREATE TEMP TABLE direct_chat_merges ON COMMIT DROP AS
            SELECT id AS old_id, canonical_id FROM (
                SELECT "Id" AS id, first_value("Id") OVER (
                    PARTITION BY "OrganizationId", "DirectParticipantKey" ORDER BY "CreatedAt", "Id") AS canonical_id
                FROM "CoreConversations" WHERE "Kind" = 'DirectHumanAgent'
                    AND "ArchivedAt" IS NULL AND "DirectParticipantKey" IS NOT NULL
            ) ranked WHERE id <> canonical_id;

            -- Preserve unread information and keep participant rows on aliases for historical identity.
            UPDATE "ConversationParticipants" p SET "LastReadMessageSequence" = combined.last_read
            FROM (
                SELECT coalesce(m.canonical_id, p."ConversationId") AS id, p."OrganizationUserId" AS member,
                    min(p."LastReadMessageSequence") AS last_read
                FROM "ConversationParticipants" p LEFT JOIN direct_chat_merges m ON m.old_id = p."ConversationId"
                WHERE p."LeftAt" IS NULL GROUP BY coalesce(m.canonical_id, p."ConversationId"), p."OrganizationUserId"
            ) combined WHERE p."ConversationId" = combined.id AND p."OrganizationUserId" = combined.member;

            -- Move history and typed references together, including references without an FK (e.g. chat turns).
            DO $merge$
            DECLARE ref record;
            BEGIN
                FOR ref IN SELECT table_schema, table_name, column_name FROM information_schema.columns
                    WHERE table_schema = current_schema() AND udt_name = 'uuid'
                    AND column_name IN ('ConversationId', 'SourceConversationId', 'OriginConversationId')
                    AND table_name <> 'ConversationParticipants'
                LOOP
                    EXECUTE format('UPDATE %I.%I r SET %I = m.canonical_id FROM direct_chat_merges m WHERE r.%I = m.old_id',
                        ref.table_schema, ref.table_name, ref.column_name, ref.column_name);
                END LOOP;
            END $merge$;

            UPDATE "CoreConversations" c SET "ArchivedAt" = now(), "MergedIntoConversationId" = m.canonical_id
            FROM direct_chat_merges m WHERE c."Id" = m.old_id;
            UPDATE "CoreConversations" SET "WorkstreamId" = NULL, "TeamId" = NULL, "Description" = NULL,
                "UpdatedAt" = greatest("UpdatedAt", coalesce((SELECT max("CreatedAt") FROM "CoreConversationMessages" m
                    WHERE m."ConversationId" = "CoreConversations"."Id"), "UpdatedAt"))
                WHERE "Kind" = 'DirectHumanAgent' AND "ArchivedAt" IS NULL AND "DirectParticipantKey" IS NOT NULL;
            """);
        migrationBuilder.CreateIndex("IX_CoreConversations_OrganizationId_DirectParticipantKey", "CoreConversations",
            ["OrganizationId", "DirectParticipantKey"], unique: true,
            filter: "\"ArchivedAt\" IS NULL AND \"DirectParticipantKey\" IS NOT NULL");
        migrationBuilder.CreateIndex("IX_CoreConversations_MergedIntoConversationId", "CoreConversations", "MergedIntoConversationId");
        migrationBuilder.AddForeignKey("FK_CoreConversations_CoreConversations_MergedIntoConversationId", "CoreConversations",
            "MergedIntoConversationId", "CoreConversations", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Combined messages stay in their canonical history; rollback does not discard messages.
        migrationBuilder.DropForeignKey("FK_CoreConversations_CoreConversations_MergedIntoConversationId", "CoreConversations");
        migrationBuilder.DropIndex("IX_CoreConversations_OrganizationId_DirectParticipantKey", "CoreConversations");
        migrationBuilder.DropIndex("IX_CoreConversations_MergedIntoConversationId", "CoreConversations");
        migrationBuilder.DropColumn("DirectParticipantKey", "CoreConversations");
        migrationBuilder.DropColumn("MergedIntoConversationId", "CoreConversations");
    }
}
