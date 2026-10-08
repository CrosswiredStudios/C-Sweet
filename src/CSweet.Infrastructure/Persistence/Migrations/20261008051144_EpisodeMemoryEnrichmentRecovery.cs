using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EpisodeMemoryEnrichmentRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetryGeneration",
                table: "MemoryEpisodeEnrichmentJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MemoryEpisodeRetryReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousGeneration = table.Column<int>(type: "integer", nullable: false),
                    RetryGeneration = table.Column<int>(type: "integer", nullable: false),
                    PreviousAttempts = table.Column<int>(type: "integer", nullable: false),
                    ReusesAcceptedExtraction = table.Column<bool>(type: "boolean", nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEpisodeRetryReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeRetryReceipts_JobId_OperationId",
                table: "MemoryEpisodeRetryReceipts",
                columns: new[] { "JobId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeRetryReceipts_JobId_RetryGeneration",
                table: "MemoryEpisodeRetryReceipts",
                columns: new[] { "JobId", "RetryGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeRetryReceipts_OrganizationId_EmployeeId_Create~",
                table: "MemoryEpisodeRetryReceipts",
                columns: new[] { "OrganizationId", "EmployeeId", "CreatedAt" });
            migrationBuilder.Sql(InstallTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "MemoryEpisodeRetryReceipts") OR
                       EXISTS(SELECT 1 FROM "MemoryEpisodeEnrichmentJobs" WHERE "RetryGeneration"<>0) THEN
                        RAISE EXCEPTION 'memory_episode_retry_downgrade_requires_snapshot';
                    END IF;
                END $$;
                DROP TRIGGER csweet_episode_retry_generation ON "MemoryEpisodeEnrichmentJobs";
                DROP TRIGGER csweet_episode_retry_commit ON "MemoryEpisodeEnrichmentJobs";
                DROP FUNCTION csweet_episode_retry_generation();
                DROP FUNCTION csweet_episode_retry_commit();
                DROP FUNCTION csweet_episode_retry_receipt_commit() CASCADE;
                DROP FUNCTION csweet_episode_retry_receipt_guard() CASCADE;
                """);
            migrationBuilder.DropTable(
                name: "MemoryEpisodeRetryReceipts");

            migrationBuilder.DropColumn(
                name: "RetryGeneration",
                table: "MemoryEpisodeEnrichmentJobs");
        }

        internal const string InstallTriggers = """
            CREATE FUNCTION csweet_episode_retry_receipt_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE job "MemoryEpisodeEnrichmentJobs"%ROWTYPE;
            BEGIN
                IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'memory_episode_retry_receipt_immutable'; END IF;
                SELECT * INTO job FROM "MemoryEpisodeEnrichmentJobs" WHERE "Id"=NEW."JobId" FOR UPDATE;
                IF job."Id" IS NULL OR job."OrganizationId"<>NEW."OrganizationId" OR job."EmployeeId"<>NEW."EmployeeId" OR
                   job."SourceHash"<>NEW."SourceHash" OR (job."AcceptedExtractionJson" IS NOT NULL)<>NEW."ReusesAcceptedExtraction" OR
                   NEW."Id"='00000000-0000-0000-0000-000000000000' OR NEW."OperationId"='00000000-0000-0000-0000-000000000000' OR
                   NEW."ActorOrganizationUserId"='00000000-0000-0000-0000-000000000000' OR NEW."ActorApplicationUserId"='00000000-0000-0000-0000-000000000000' OR
                   NEW."PreviousGeneration"<0 OR NEW."PreviousGeneration"=2147483647 OR NEW."RetryGeneration"<>NEW."PreviousGeneration"+1 OR
                   NEW."PreviousAttempts"<0 OR job."RetryGeneration" NOT IN (NEW."PreviousGeneration",NEW."RetryGeneration") THEN
                    RAISE EXCEPTION 'memory_episode_retry_receipt_invalid';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_episode_retry_receipt_guard BEFORE INSERT OR UPDATE OR DELETE ON "MemoryEpisodeRetryReceipts"
                FOR EACH ROW EXECUTE FUNCTION csweet_episode_retry_receipt_guard();
            CREATE FUNCTION csweet_episode_retry_generation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP='INSERT' THEN
                    IF NEW."RetryGeneration"<>0 THEN RAISE EXCEPTION 'memory_episode_retry_generation_invalid'; END IF;
                ELSIF NEW."RetryGeneration" IS DISTINCT FROM OLD."RetryGeneration" THEN
                    IF OLD."RetryGeneration"=2147483647 OR NEW."RetryGeneration"<>OLD."RetryGeneration"+1 OR OLD."Status"<>'Failed' OR
                       OLD."LeaseToken" IS NOT NULL OR OLD."CompletedAt" IS NOT NULL OR NEW."Status"<>'Pending' OR
                       NEW."Attempts"<>0 OR NEW."LeaseToken" IS NOT NULL OR NEW."LeaseExpiresAt" IS NOT NULL OR NEW."CompletedAt" IS NOT NULL THEN
                        RAISE EXCEPTION 'memory_episode_retry_generation_invalid';
                    END IF;
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_episode_retry_generation BEFORE INSERT OR UPDATE ON "MemoryEpisodeEnrichmentJobs"
                FOR EACH ROW EXECUTE FUNCTION csweet_episode_retry_generation();
            CREATE FUNCTION csweet_episode_retry_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."RetryGeneration" IS DISTINCT FROM OLD."RetryGeneration" AND NOT EXISTS(
                    SELECT 1 FROM "MemoryEpisodeRetryReceipts" r WHERE r."JobId"=NEW."Id" AND
                    r."PreviousGeneration"=OLD."RetryGeneration" AND r."RetryGeneration"=NEW."RetryGeneration" AND
                    r."PreviousAttempts"=OLD."Attempts" AND r."SourceHash"=NEW."SourceHash") THEN
                    RAISE EXCEPTION 'memory_episode_retry_receipt_required';
                END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER csweet_episode_retry_commit AFTER UPDATE ON "MemoryEpisodeEnrichmentJobs"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION csweet_episode_retry_commit();
            CREATE FUNCTION csweet_episode_retry_receipt_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NOT EXISTS(SELECT 1 FROM "MemoryEpisodeEnrichmentJobs" j WHERE j."Id"=NEW."JobId" AND
                    j."RetryGeneration"=NEW."RetryGeneration" AND j."Status"='Pending' AND j."Attempts"=0 AND
                    j."LeaseToken" IS NULL AND j."LeaseExpiresAt" IS NULL AND j."CompletedAt" IS NULL) THEN
                    RAISE EXCEPTION 'memory_episode_retry_transition_required';
                END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER csweet_episode_retry_receipt_commit AFTER INSERT ON "MemoryEpisodeRetryReceipts"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION csweet_episode_retry_receipt_commit();
            """;
    }
}
