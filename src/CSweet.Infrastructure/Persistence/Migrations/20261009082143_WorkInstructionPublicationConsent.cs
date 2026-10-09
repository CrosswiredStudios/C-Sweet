using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkInstructionPublicationConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkInstructionPublications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceChecksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SelectionOffset = table.Column<int>(type: "integer", nullable: false),
                    SelectionLength = table.Column<int>(type: "integer", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublishedBoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommentId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstructionChecksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkInstructionPublications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkInstructionPublications_CommentId",
                table: "WorkInstructionPublications",
                column: "CommentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkInstructionPublications_OrganizationId_ActorApplication~",
                table: "WorkInstructionPublications",
                columns: new[] { "OrganizationId", "ActorApplicationUserId", "WorkItemId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkInstructionPublications_OrganizationId_OperationId",
                table: "WorkInstructionPublications",
                columns: new[] { "OrganizationId", "OperationId" },
                unique: true);
            migrationBuilder.Sql(InstallGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN IF EXISTS(SELECT 1 FROM "WorkInstructionPublications") THEN
                    RAISE EXCEPTION 'work_instruction_consent_downgrade_refused'; END IF; END $$;
                DROP TRIGGER work_instruction_comment_pair ON "WorkItemComments";
                DROP TRIGGER work_instruction_comment_binding ON "WorkItemComments";
                DROP FUNCTION work_instruction_comment_guard();
                DROP TRIGGER work_instruction_receipt_pair ON "WorkInstructionPublications";
                DROP FUNCTION work_instruction_receipt_pair();
                DROP FUNCTION work_instruction_receipt_freeze() CASCADE;
                """);
            migrationBuilder.DropTable(
                name: "WorkInstructionPublications");
        }

        public const string InstallGuards = """
            CREATE FUNCTION work_instruction_receipt_freeze() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'work_instruction_consent_immutable'; END $$;
            CREATE TRIGGER work_instruction_receipt_freeze BEFORE UPDATE OR DELETE ON "WorkInstructionPublications"
                FOR EACH ROW EXECUTE FUNCTION work_instruction_receipt_freeze();
            CREATE TRIGGER work_instruction_receipt_truncate BEFORE TRUNCATE ON "WorkInstructionPublications"
                FOR EACH STATEMENT EXECUTE FUNCTION work_instruction_receipt_freeze();
            CREATE FUNCTION work_instruction_receipt_pair() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."OperationId"='00000000-0000-0000-0000-000000000000'::uuid OR
                    NEW."SelectionOffset"<0 OR NEW."SelectionLength" NOT BETWEEN 1 AND 8192 OR
                    length(NEW."SourceChecksum")<>64 OR NEW."SourceChecksum" !~ '^[a-f0-9]+$' OR
                    length(NEW."InstructionChecksum")<>64 OR NEW."InstructionChecksum" !~ '^[a-f0-9]+$' OR
                    length(NEW."RequestHash")<>64 OR NEW."RequestHash" !~ '^[a-f0-9]+$' OR NOT EXISTS(
                    SELECT 1 FROM "WorkItemComments" c JOIN "CoreWorkTasks" w ON w."Id"=c."WorkItemId"
                    JOIN "CoreOrganizationUsers" a ON a."Id"=c."AuthorSubjectId"
                    JOIN "CoreConversationMessages" m ON m."Id"=NEW."SourceMessageId"
                    JOIN "CoreConversations" v ON v."Id"=m."ConversationId"
                    WHERE c."Id"=NEW."CommentId" AND c."OrganizationId"=NEW."OrganizationId" AND
                    c."WorkItemId"=NEW."WorkItemId" AND w."OrganizationId"=NEW."OrganizationId" AND
                    w."BoardId"=NEW."PublishedBoardId" AND c."AuthorKind"='OrganizationUser' AND
                    c."AuthorSubjectId"=NEW."ActorOrganizationUserId" AND a."EmployeeType"='Human' AND
                    a."OrganizationId"=NEW."OrganizationId" AND a."ApplicationUserId"=NEW."ActorApplicationUserId" AND
                    c."Kind"='human.instruction' AND c."CausationId"=NEW."Id"::text AND
                    c."ArtifactDigest"=NEW."InstructionChecksum" AND c."Revision"=1 AND c."DeletedAt" IS NULL AND
                    encode(sha256(convert_to(c."Body",'UTF8')),'hex')=NEW."InstructionChecksum" AND
                    v."Id"=NEW."SourceConversationId" AND v."OrganizationId"=NEW."OrganizationId" AND
                    v."Kind"='DirectHumanAgent' AND v."AgentOrganizationUserId"=NEW."SourceEmployeeId" AND
                    v."InitiatedByOrganizationUserId"=NEW."ActorOrganizationUserId" AND m."Role"='User' AND
                    m."SenderOrganizationUserId"=NEW."ActorOrganizationUserId" AND
                    encode(sha256(convert_to(m."Content",'UTF8')),'hex')=NEW."SourceChecksum" AND
                    substring(m."Content" FROM NEW."SelectionOffset"+1 FOR NEW."SelectionLength")=c."Body") THEN
                    RAISE EXCEPTION 'work_instruction_consent_binding_invalid'; END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER work_instruction_receipt_pair AFTER INSERT ON "WorkInstructionPublications"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION work_instruction_receipt_pair();
            CREATE FUNCTION work_instruction_comment_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP='UPDATE' THEN
                    IF OLD."Kind"='human.instruction' AND ROW(NEW."OrganizationId",NEW."WorkItemId",NEW."AuthorKind",
                        NEW."AuthorSubjectId",NEW."Kind",NEW."CausationId",NEW."ArtifactDigest") IS DISTINCT FROM
                        ROW(OLD."OrganizationId",OLD."WorkItemId",OLD."AuthorKind",OLD."AuthorSubjectId",
                        OLD."Kind",OLD."CausationId",OLD."ArtifactDigest") OR
                        OLD."Kind" IS DISTINCT FROM 'human.instruction' AND NEW."Kind"='human.instruction' THEN
                        RAISE EXCEPTION 'work_instruction_comment_binding_immutable'; END IF;
                ELSIF NEW."Kind"='human.instruction' AND NOT EXISTS(SELECT 1 FROM "WorkInstructionPublications" p
                    WHERE p."CommentId"=NEW."Id" AND p."Id"::text=NEW."CausationId") THEN
                    RAISE EXCEPTION 'work_instruction_consent_required';
                END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER work_instruction_comment_pair AFTER INSERT ON "WorkItemComments"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION work_instruction_comment_guard();
            CREATE TRIGGER work_instruction_comment_binding BEFORE UPDATE ON "WorkItemComments"
                FOR EACH ROW EXECUTE FUNCTION work_instruction_comment_guard();
            """;
    }
}
