using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewedEpisodeReextraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_EpisodeId",
                table: "MemoryEpisodeEnrichmentJobs");

            migrationBuilder.AddColumn<int>(
                name: "InputGeneration",
                table: "MemoryEpisodeEnrichmentJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PreviousJobId",
                table: "MemoryEpisodeEnrichmentJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAt",
                table: "MemoryEpisodeEnrichmentJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemoryEpisodeReextractionReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    InputGeneration = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreviousJobHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreviousAcceptedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEpisodeReextractionReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_EpisodeId",
                table: "MemoryEpisodeEnrichmentJobs",
                column: "EpisodeId",
                unique: true,
                filter: "\"SupersededAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_EpisodeId_InputGeneration",
                table: "MemoryEpisodeEnrichmentJobs",
                columns: new[] { "EpisodeId", "InputGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_PreviousJobId",
                table: "MemoryEpisodeEnrichmentJobs",
                column: "PreviousJobId",
                unique: true,
                filter: "\"PreviousJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeReextractionReceipts_JobId",
                table: "MemoryEpisodeReextractionReceipts",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeReextractionReceipts_OrganizationId_EpisodeId_~",
                table: "MemoryEpisodeReextractionReceipts",
                columns: new[] { "OrganizationId", "EpisodeId", "InputGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeReextractionReceipts_OrganizationId_OperationId",
                table: "MemoryEpisodeReextractionReceipts",
                columns: new[] { "OrganizationId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeReextractionReceipts_PreviousJobId",
                table: "MemoryEpisodeReextractionReceipts",
                column: "PreviousJobId",
                unique: true);
            migrationBuilder.Sql(EpisodeReextractionGuards.Install);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS(SELECT 1 FROM "MemoryEpisodeReextractionReceipts") OR EXISTS(
                    SELECT 1 FROM "MemoryEpisodeEnrichmentJobs" WHERE "InputGeneration"<>0 OR "PreviousJobId" IS NOT NULL OR "SupersededAt" IS NOT NULL) THEN
                    RAISE EXCEPTION 'memory_episode_reextraction_downgrade_requires_snapshot';
                  END IF;
                END $$;
                DROP TRIGGER csweet_episode_reextraction_commit ON "MemoryEpisodeEnrichmentJobs";
                DROP TRIGGER csweet_episode_reextraction_guard ON "MemoryEpisodeEnrichmentJobs";
                DROP TRIGGER csweet_episode_reextraction_receipt_commit ON "MemoryEpisodeReextractionReceipts";
                DROP TRIGGER csweet_episode_reextraction_receipt_guard ON "MemoryEpisodeReextractionReceipts";
                DROP FUNCTION csweet_episode_reextraction_commit();
                DROP FUNCTION csweet_episode_reextraction_guard();
                DROP FUNCTION csweet_episode_reextraction_receipt_guard();
                DROP FUNCTION csweet_episode_reextraction_transition_valid(uuid);
                """);
            migrationBuilder.Sql(EpisodeReextractionGuards.RestoreOutputGuard);
            migrationBuilder.DropTable(
                name: "MemoryEpisodeReextractionReceipts");

            migrationBuilder.DropIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_EpisodeId",
                table: "MemoryEpisodeEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_EpisodeId_InputGeneration",
                table: "MemoryEpisodeEnrichmentJobs");

            migrationBuilder.DropIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_PreviousJobId",
                table: "MemoryEpisodeEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "InputGeneration",
                table: "MemoryEpisodeEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "PreviousJobId",
                table: "MemoryEpisodeEnrichmentJobs");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "MemoryEpisodeEnrichmentJobs");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEpisodeEnrichmentJobs_EpisodeId",
                table: "MemoryEpisodeEnrichmentJobs",
                column: "EpisodeId",
                unique: true);
        }
    }
}
