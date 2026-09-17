using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkEfficiencyAndBenchmarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentPackageVersion",
                table: "AgentRunLogs",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentWorkItemId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AncestorWorkItemIdsJson",
                table: "AgentRunLogs",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "AttributionKind",
                table: "AgentRunLogs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<Guid>(
                name: "BenchmarkTrialId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfigurationDigest",
                table: "AgentRunLogs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InferenceSettingsJson",
                table: "AgentRunLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeasurementKind",
                table: "AgentRunLogs",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProviderStartedAt",
                table: "AgentRunLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QueueJobId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReportedInputTokens",
                table: "AgentRunLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReportedOutputTokens",
                table: "AgentRunLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkItemId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstreamId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            // Reconstruct only the immutable TaskRun -> WorkTask link. Current parents/boards
            // cannot establish historical ownership and are intentionally not backfilled.
            migrationBuilder.Sql("""
                UPDATE "AgentRunLogs" AS l
                SET "WorkItemId" = t."Id", "AttributionKind" = 'LegacyTaskRun'
                FROM "CoreTaskRuns" AS r
                JOIN "CoreWorkTasks" AS t ON t."Id" = r."TaskId"
                WHERE l."TaskRunId" = r."Id" AND l."OrganizationId" = t."OrganizationId"
                  AND l."InvocationKind" <> 'llm-queue';
                """);

            migrationBuilder.CreateTable(
                name: "BenchmarkDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    BlueprintJson = table.Column<string>(type: "text", nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    Digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BenchmarkWakes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrialId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkWakes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkLifecycleEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PreviousStatus = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Status = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkLifecycleEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BenchmarkCampaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Scheduling = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Repetitions = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkCampaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BenchmarkCampaigns_BenchmarkDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "BenchmarkDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BenchmarkTrials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: true),
                    VariantIndex = table.Column<int>(type: "integer", nullable: false),
                    Repetition = table.Column<int>(type: "integer", nullable: false),
                    ExecutionOrder = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EvaluationStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SubmissionJson = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeclaredCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextRecoveryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkTrials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BenchmarkTrials_BenchmarkCampaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "BenchmarkCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BenchmarkAssessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CriterionKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Score = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: true),
                    Passed = table.Column<bool>(type: "boolean", nullable: true),
                    Rationale = table.Column<string>(type: "text", nullable: false),
                    EvidenceReferencesJson = table.Column<string>(type: "text", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvaluatorVersion = table.Column<string>(type: "text", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BenchmarkAssessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BenchmarkAssessments_BenchmarkTrials_TrialId",
                        column: x => x.TrialId,
                        principalTable: "BenchmarkTrials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunLogs_BenchmarkTrialId_StartedAt",
                table: "AgentRunLogs",
                columns: new[] { "BenchmarkTrialId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunLogs_OrganizationId_WorkItemId_StartedAt",
                table: "AgentRunLogs",
                columns: new[] { "OrganizationId", "WorkItemId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunLogs_OrganizationId_WorkstreamId_StartedAt",
                table: "AgentRunLogs",
                columns: new[] { "OrganizationId", "WorkstreamId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkAssessments_TrialId_IdempotencyKey",
                table: "BenchmarkAssessments",
                columns: new[] { "TrialId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkCampaigns_CreatedBy_IdempotencyKey",
                table: "BenchmarkCampaigns",
                columns: new[] { "CreatedBy", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkCampaigns_DefinitionId",
                table: "BenchmarkCampaigns",
                column: "DefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkDefinitions_FamilyId_Version",
                table: "BenchmarkDefinitions",
                columns: new[] { "FamilyId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkTrials_CampaignId_VariantIndex_Repetition",
                table: "BenchmarkTrials",
                columns: new[] { "CampaignId", "VariantIndex", "Repetition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkTrials_OrganizationId",
                table: "BenchmarkTrials",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkTrials_Status_NextRecoveryAt",
                table: "BenchmarkTrials",
                columns: new[] { "Status", "NextRecoveryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BenchmarkWakes_ProcessedAt_CreatedAt",
                table: "BenchmarkWakes",
                columns: new[] { "ProcessedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkLifecycleEvents_OrganizationId_ResourceId_OccurredAt",
                table: "WorkLifecycleEvents",
                columns: new[] { "OrganizationId", "ResourceId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BenchmarkAssessments");

            migrationBuilder.DropTable(
                name: "BenchmarkWakes");

            migrationBuilder.DropTable(
                name: "WorkLifecycleEvents");

            migrationBuilder.DropTable(
                name: "BenchmarkTrials");

            migrationBuilder.DropTable(
                name: "BenchmarkCampaigns");

            migrationBuilder.DropTable(
                name: "BenchmarkDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_AgentRunLogs_BenchmarkTrialId_StartedAt",
                table: "AgentRunLogs");

            migrationBuilder.DropIndex(
                name: "IX_AgentRunLogs_OrganizationId_WorkItemId_StartedAt",
                table: "AgentRunLogs");

            migrationBuilder.DropIndex(
                name: "IX_AgentRunLogs_OrganizationId_WorkstreamId_StartedAt",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "AgentPackageVersion",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "AgentWorkItemId",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "AncestorWorkItemIdsJson",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "AttributionKind",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "BenchmarkTrialId",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "ConfigurationDigest",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "InferenceSettingsJson",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "MeasurementKind",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "ProviderStartedAt",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "QueueJobId",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "ReportedInputTokens",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "ReportedOutputTokens",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "WorkItemId",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "WorkstreamId",
                table: "AgentRunLogs");
        }
    }
}
