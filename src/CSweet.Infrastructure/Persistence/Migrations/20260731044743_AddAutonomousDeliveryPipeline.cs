using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutonomousDeliveryPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_ParentWorkTaskId",
                table: "CoreWorkTasks");

            migrationBuilder.AddColumn<int>(
                name: "Sequence",
                table: "WorkSprints",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeCommitSha",
                table: "GitTicketWorkspaces",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeStatus",
                table: "GitTicketWorkspaces",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergedAt",
                table: "GitTicketWorkspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CanMergeQaApprovedPullRequest",
                table: "GitRepositoryConnectionGrants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "GitRepositoryConnectionGrants",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "DeliverySpecificationJson",
                table: "CoreWorkTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsQaTrackingDefect",
                table: "CoreWorkTasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "MergeAuthorizationGrantId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MergeAuthorizationGrantRevision",
                table: "CoreWorkTasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeCommitSha",
                table: "CoreWorkTasks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MergeQualityRunId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MergeStatus",
                table: "CoreWorkTasks",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MergedAt",
                table: "CoreWorkTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityBriefJson",
                table: "CoreWorkTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QualityCycle",
                table: "CoreWorkTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "QualityFindingFingerprint",
                table: "CoreWorkTasks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkDeliveryPipelines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeveloperInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    QualityInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DevelopmentColumnId = table.Column<Guid>(type: "uuid", nullable: false),
                    QualityColumnId = table.Column<Guid>(type: "uuid", nullable: false),
                    DoneColumnId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaseBranch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MergeStrategy = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActiveSprintId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActiveWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    QualityCycle = table.Column<int>(type: "integer", nullable: false),
                    MergeStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SourcePullRequestUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SourceCommitSha = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    ResumeAction = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ConsecutiveInfrastructureFailures = table.Column<int>(type: "integer", nullable: false),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDeliveryPipelines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryPipelines_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryPipelines_CoreWorkTasks_ActiveWorkItemId",
                        column: x => x.ActiveWorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryPipelines_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryPipelines_WorkSprints_ActiveSprintId",
                        column: x => x.ActiveSprintId,
                        principalTable: "WorkSprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemDependencies",
                columns: table => new
                {
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependsOnWorkItemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemDependencies", x => new { x.WorkItemId, x.DependsOnWorkItemId });
                    table.CheckConstraint("CK_WorkItemDependencies_NotSelf", "\"WorkItemId\" <> \"DependsOnWorkItemId\"");
                    table.ForeignKey(
                        name: "FK_WorkItemDependencies_CoreWorkTasks_DependsOnWorkItemId",
                        column: x => x.DependsOnWorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItemDependencies_CoreWorkTasks_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkQualityRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    QualityInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentRevision = table.Column<long>(type: "bigint", nullable: false),
                    QualityCycle = table.Column<int>(type: "integer", nullable: false),
                    SourceCommitSha = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Verdict = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkQualityRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkQualityRuns_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkQualityRuns_CoreWorkTasks_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkQualityRuns_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprints_BoardId_Sequence",
                table: "WorkSprints",
                columns: new[] { "BoardId", "Sequence" },
                unique: true,
                filter: "\"Sequence\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_ParentWorkTaskId_QualityFindingFingerprint",
                table: "CoreWorkTasks",
                columns: new[] { "ParentWorkTaskId", "QualityFindingFingerprint" },
                unique: true,
                filter: "\"QualityFindingFingerprint\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryPipelines_ActiveSprintId",
                table: "WorkDeliveryPipelines",
                column: "ActiveSprintId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryPipelines_ActiveWorkItemId",
                table: "WorkDeliveryPipelines",
                column: "ActiveWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryPipelines_BoardId",
                table: "WorkDeliveryPipelines",
                column: "BoardId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryPipelines_OrganizationId_IsEnabled_Status",
                table: "WorkDeliveryPipelines",
                columns: new[] { "OrganizationId", "IsEnabled", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemDependencies_DependsOnWorkItemId",
                table: "WorkItemDependencies",
                column: "DependsOnWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkQualityRuns_BoardId",
                table: "WorkQualityRuns",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkQualityRuns_OrganizationId",
                table: "WorkQualityRuns",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkQualityRuns_QualityInstallationId_WorkItemId_Idempotenc~",
                table: "WorkQualityRuns",
                columns: new[] { "QualityInstallationId", "WorkItemId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkQualityRuns_WorkItemId_QualityCycle",
                table: "WorkQualityRuns",
                columns: new[] { "WorkItemId", "QualityCycle" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkDeliveryPipelines");

            migrationBuilder.DropTable(
                name: "WorkItemDependencies");

            migrationBuilder.DropTable(
                name: "WorkQualityRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkSprints_BoardId_Sequence",
                table: "WorkSprints");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_ParentWorkTaskId_QualityFindingFingerprint",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "WorkSprints");

            migrationBuilder.DropColumn(
                name: "MergeCommitSha",
                table: "GitTicketWorkspaces");

            migrationBuilder.DropColumn(
                name: "MergeStatus",
                table: "GitTicketWorkspaces");

            migrationBuilder.DropColumn(
                name: "MergedAt",
                table: "GitTicketWorkspaces");

            migrationBuilder.DropColumn(
                name: "CanMergeQaApprovedPullRequest",
                table: "GitRepositoryConnectionGrants");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "GitRepositoryConnectionGrants");

            migrationBuilder.DropColumn(
                name: "DeliverySpecificationJson",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "IsQaTrackingDefect",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "MergeAuthorizationGrantId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "MergeAuthorizationGrantRevision",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "MergeCommitSha",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "MergeQualityRunId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "MergeStatus",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "MergedAt",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "QualityBriefJson",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "QualityCycle",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "QualityFindingFingerprint",
                table: "CoreWorkTasks");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_ParentWorkTaskId",
                table: "CoreWorkTasks",
                column: "ParentWorkTaskId");
        }
    }
}
