using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableEpisodeMemoryEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryEpisodeEnrichmentJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerApplicationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceJson = table.Column<string>(type: "text", nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedExtractionJson = table.Column<string>(type: "jsonb", nullable: true),
                    ExtractionAcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEpisodeEnrichmentJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MemoryEpisodeExtractionReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEpisodeExtractionReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemoryEpisodeExtractionReceipts_MemoryEpisodeEnrichmentJobs~",
                        column: x => x.JobId,
                        principalTable: "MemoryEpisodeEnrichmentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_EpisodeId",
                table: "MemoryEpisodeEnrichmentJobs",
                column: "EpisodeId",
                unique: true);
            migrationBuilder.Sql(InstallTriggers);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_OrganizationId_LastAttemptAt",
                table: "MemoryEpisodeEnrichmentJobs",
                columns: new[] { "OrganizationId", "LastAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_Status_NextAttemptAt",
                table: "MemoryEpisodeEnrichmentJobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeExtractionReceipts_JobId_LeaseToken",
                table: "MemoryEpisodeExtractionReceipts",
                columns: new[] { "JobId", "LeaseToken" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "MemoryEpisodeEnrichmentJobs") THEN
                        RAISE EXCEPTION 'memory_episode_jobs_downgrade_requires_snapshot';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "MemoryEpisodeExtractionReceipts");

            migrationBuilder.DropTable(
                name: "MemoryEpisodeEnrichmentJobs");
            migrationBuilder.Sql("DROP FUNCTION csweet_episode_job_guard(); DROP FUNCTION csweet_episode_receipt_guard();");
        }

        internal const string InstallTriggers = """
            CREATE FUNCTION csweet_episode_job_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP='INSERT' AND (NEW."Status"<>'Pending' OR NEW."Attempts"<>0 OR NEW."LeaseToken" IS NOT NULL OR
                    NEW."LeaseExpiresAt" IS NOT NULL OR NEW."AcceptedExtractionJson" IS NOT NULL OR NEW."ExtractionAcceptedAt" IS NOT NULL) THEN
                    RAISE EXCEPTION 'memory_episode_input_invalid';
                END IF;
                IF TG_OP='UPDATE' THEN
                    IF (NEW."OrganizationId",NEW."EmployeeId",NEW."InstallationId",NEW."ReviewerApplicationUserId",
                        NEW."EpisodeId",NEW."SourceJson",NEW."SourceHash",NEW."CreatedAt") IS DISTINCT FROM
                       (OLD."OrganizationId",OLD."EmployeeId",OLD."InstallationId",OLD."ReviewerApplicationUserId",
                        OLD."EpisodeId",OLD."SourceJson",OLD."SourceHash",OLD."CreatedAt") OR
                       (OLD."AcceptedExtractionJson" IS NOT NULL AND (NEW."AcceptedExtractionJson",NEW."ExtractionAcceptedAt") IS DISTINCT FROM
                        (OLD."AcceptedExtractionJson",OLD."ExtractionAcceptedAt")) THEN
                        RAISE EXCEPTION 'memory_episode_input_immutable';
                    END IF;
                    IF OLD."AcceptedExtractionJson" IS NULL AND NEW."AcceptedExtractionJson" IS NOT NULL AND
                       (NEW."Status"<>'Processing' OR NEW."LeaseToken" IS NULL OR NEW."LeaseExpiresAt" IS NULL OR NEW."LeaseExpiresAt"<=clock_timestamp() OR
                        NEW."ExtractionAcceptedAt" IS NULL OR octet_length(NEW."AcceptedExtractionJson"::text)>1048576) THEN
                        RAISE EXCEPTION 'memory_episode_output_lease_lost';
                    END IF;
                END IF;
                IF NEW."SourceHash"<>lower(encode(sha256(convert_to(NEW."SourceJson",'UTF8')),'hex')) OR
                   octet_length(NEW."SourceJson")>1048576 OR NEW."Attempts"<0 THEN
                    RAISE EXCEPTION 'memory_episode_input_invalid';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_episode_job_guard BEFORE INSERT OR UPDATE ON "MemoryEpisodeEnrichmentJobs"
                FOR EACH ROW EXECUTE FUNCTION csweet_episode_job_guard();
            CREATE FUNCTION csweet_episode_receipt_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE job "MemoryEpisodeEnrichmentJobs"%ROWTYPE;
            BEGIN
                IF TG_OP='UPDATE' THEN RAISE EXCEPTION 'memory_episode_receipt_immutable'; END IF;
                IF TG_OP='DELETE' THEN
                    IF EXISTS(SELECT 1 FROM "MemoryEpisodeEnrichmentJobs" WHERE "Id"=OLD."JobId") THEN
                        RAISE EXCEPTION 'memory_episode_receipt_immutable';
                    END IF;
                    RETURN OLD;
                END IF;
                SELECT * INTO job FROM "MemoryEpisodeEnrichmentJobs" WHERE "Id"=NEW."JobId" FOR UPDATE;
                IF job."Id" IS NULL OR job."Status"<>'Processing' OR job."LeaseToken" IS DISTINCT FROM NEW."LeaseToken" OR
                    job."LeaseExpiresAt" IS NULL OR job."LeaseExpiresAt"<=clock_timestamp() OR NEW."SourceHash"<>job."SourceHash" OR
                    NEW."Id"='00000000-0000-0000-0000-000000000000' OR NEW."LeaseToken"='00000000-0000-0000-0000-000000000000' THEN
                    RAISE EXCEPTION 'memory_episode_receipt_lease_lost';
                END IF;
                IF (SELECT count(*) FROM "MemoryEpisodeExtractionReceipts" WHERE "JobId"=NEW."JobId")>=64 THEN
                    RAISE EXCEPTION 'memory_episode_receipt_capacity';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_episode_receipt_guard BEFORE INSERT OR UPDATE OR DELETE ON "MemoryEpisodeExtractionReceipts"
                FOR EACH ROW EXECUTE FUNCTION csweet_episode_receipt_guard();
            """;
    }
}
