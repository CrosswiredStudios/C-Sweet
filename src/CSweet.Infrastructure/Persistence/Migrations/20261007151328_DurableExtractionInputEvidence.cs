using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableExtractionInputEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryExtractionInputReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: false),
                    RetryGeneration = table.Column<int>(type: "integer", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    ReceiptHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryExtractionInputReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryExtractionInputReceipts_MemoryCaptureOutbox_JobId",
                        column: x => x.JobId,
                        principalTable: "MemoryCaptureOutbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryExtractionInputReceipts_JobId_LeaseToken",
                table: "MemoryExtractionInputReceipts",
                columns: new[] { "JobId", "LeaseToken" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryExtractionInputReceipts_OrganizationId",
                table: "MemoryExtractionInputReceipts",
                column: "OrganizationId");
            migrationBuilder.Sql(InstallTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "MemoryExtractionInputReceipts") THEN
                        RAISE EXCEPTION 'memory_input_evidence_downgrade_requires_snapshot';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "MemoryExtractionInputReceipts");
            migrationBuilder.Sql("DROP FUNCTION csweet_extraction_input_guard();");
        }

        internal const string InstallTriggers = """
            CREATE FUNCTION csweet_extraction_input_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE job "MemoryCaptureOutbox"%ROWTYPE; organization uuid;
            BEGIN
                IF TG_OP='UPDATE' THEN RAISE EXCEPTION 'memory_input_evidence_immutable'; END IF;
                IF TG_OP='DELETE' THEN
                    IF EXISTS(SELECT 1 FROM "MemoryCaptureOutbox" WHERE "Id"=OLD."JobId") THEN
                        RAISE EXCEPTION 'memory_input_evidence_immutable';
                    END IF;
                    RETURN OLD;
                END IF;
                SELECT * INTO job FROM "MemoryCaptureOutbox" WHERE "Id"=NEW."JobId" FOR UPDATE;
                SELECT c."OrganizationId" INTO organization FROM "CoreConversationMessages" m
                    JOIN "CoreConversations" c ON c."Id"=m."ConversationId" WHERE m."Id"=job."ConversationMessageId";
                IF job."Id" IS NULL OR job."Status"<>'Processing' OR job."LeaseToken" IS DISTINCT FROM NEW."LeaseToken" OR
                    job."RetryGeneration"<>NEW."RetryGeneration" OR job."LeaseExpiresAt" IS NULL OR job."LeaseExpiresAt"<=clock_timestamp() OR
                    organization IS DISTINCT FROM NEW."OrganizationId" OR NEW."RetryGeneration"<0 OR
                    NEW."Id"='00000000-0000-0000-0000-000000000000' OR NEW."LeaseToken"='00000000-0000-0000-0000-000000000000' OR
                    octet_length(NEW."EvidenceJson")>16384 OR
                    NEW."ReceiptHash"<>upper(encode(sha256(convert_to(NEW."EvidenceJson", 'UTF8')), 'hex')) OR
                    EXISTS(SELECT 1 FROM "MemorySourceInvalidations" WHERE "SourceMessageId"=job."ConversationMessageId") OR
                    EXISTS(SELECT 1 FROM jsonb_array_elements(NEW."EvidenceJson"::jsonb->'Sources'->'Messages') s
                        JOIN "MemorySourceInvalidations" i ON i."SourceMessageId"=(s->>'Id')::uuid) THEN
                    RAISE EXCEPTION 'memory_input_evidence_lease_lost';
                END IF;
                IF (SELECT count(*) FROM "MemoryExtractionInputReceipts" WHERE "JobId"=NEW."JobId")>=64 THEN
                    RAISE EXCEPTION 'memory_input_evidence_capacity';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_extraction_input_guard BEFORE INSERT OR UPDATE OR DELETE ON "MemoryExtractionInputReceipts"
                FOR EACH ROW EXECUTE FUNCTION csweet_extraction_input_guard();
            """;
    }
}
