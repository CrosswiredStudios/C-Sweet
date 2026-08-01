using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardWorkOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkAutomationExecutions");

            migrationBuilder.DropTable(
                name: "WorkDeliveryPipelines");

            migrationBuilder.DropTable(
                name: "WorkAutomationRules");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_BoardId",
                table: "CoreWorkTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CoreWorkTasks_Status",
                table: "CoreWorkTasks");

            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "WorkBoards",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ManagerOrganizationUserId",
                table: "WorkBoards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextItemSequence",
                table: "WorkBoards",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "AccountableOrganizationUserId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Identifier",
                table: "CoreWorkTasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IdentifierSequence",
                table: "CoreWorkTasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkItemStageAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrincipalKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlatformAction = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemStageAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemStageAssignments_AgentInstallations_AgentInstallati~",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItemStageAssignments_CoreOrganizationUsers_Organization~",
                        column: x => x.OrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItemStageAssignments_CoreWorkTasks_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrchestrationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SprintExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    StageExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrchestrationEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrchestrationPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PublishedRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrchestrationPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrchestrationPolicies_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrchestrationPolicyRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    InitialStageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MergeMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GlobalConcurrencyLimit = table.Column<int>(type: "integer", nullable: false),
                    OrganizationConcurrencyLimit = table.Column<int>(type: "integer", nullable: false),
                    BoardConcurrencyLimit = table.Column<int>(type: "integer", nullable: false),
                    DefaultStageConcurrencyLimit = table.Column<int>(type: "integer", nullable: false),
                    DefaultAssigneeConcurrencyLimit = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrchestrationPolicyRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrchestrationPolicyRevisions_WorkOrchestrationPolicies_~",
                        column: x => x.PolicyId,
                        principalTable: "WorkOrchestrationPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrchestrationStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ColumnId = table.Column<Guid>(type: "uuid", nullable: true),
                    Instructions = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    InputSchemaJson = table.Column<string>(type: "jsonb", nullable: false),
                    OutputSchemaJson = table.Column<string>(type: "jsonb", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    ConcurrencyLimit = table.Column<int>(type: "integer", nullable: true),
                    MaximumAttempts = table.Column<int>(type: "integer", nullable: false),
                    InitialRetryDelaySeconds = table.Column<int>(type: "integer", nullable: false),
                    MaximumRetryDelaySeconds = table.Column<int>(type: "integer", nullable: false),
                    PlatformAction = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    IsSuccessfulTerminal = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrchestrationStages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrchestrationStages_WorkBoardColumns_ColumnId",
                        column: x => x.ColumnId,
                        principalTable: "WorkBoardColumns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrchestrationStages_WorkOrchestrationPolicyRevisions_Po~",
                        column: x => x.PolicyRevisionId,
                        principalTable: "WorkOrchestrationPolicyRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrchestrationTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OutcomeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ToStageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MaximumTraversals = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrchestrationTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrchestrationTransitions_WorkOrchestrationPolicyRevisio~",
                        column: x => x.PolicyRevisionId,
                        principalTable: "WorkOrchestrationPolicyRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkSprintExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SprintId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PolicySnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    AssignmentSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSprintExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkSprintExecutions_WorkOrchestrationPolicyRevisions_Polic~",
                        column: x => x.PolicyRevisionId,
                        principalTable: "WorkOrchestrationPolicyRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkSprintExecutions_WorkSprints_SprintId",
                        column: x => x.SprintId,
                        principalTable: "WorkSprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SprintExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemIdentifier = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentStageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Traversal = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BlockedReason = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemExecutions_CoreWorkTasks_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItemExecutions_WorkSprintExecutions_SprintExecutionId",
                        column: x => x.SprintExecutionId,
                        principalTable: "WorkSprintExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkStageExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StageType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Traversal = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PrincipalKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlatformAction = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    LastOutcomeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSummary = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    RetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkStageExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkStageExecutions_WorkItemExecutions_ItemExecutionId",
                        column: x => x.ItemExecutionId,
                        principalTable: "WorkItemExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkExecutionAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StageExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorCategory = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkExecutionAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkExecutionAttempts_AgentWorkItems_AgentWorkItemId",
                        column: x => x.AgentWorkItemId,
                        principalTable: "AgentWorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkExecutionAttempts_WorkStageExecutions_StageExecutionId",
                        column: x => x.StageExecutionId,
                        principalTable: "WorkStageExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoards_ManagerOrganizationUserId",
                table: "WorkBoards",
                column: "ManagerOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoards_OrganizationId_Key",
                table: "WorkBoards",
                columns: new[] { "OrganizationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_AccountableOrganizationUserId",
                table: "CoreWorkTasks",
                column: "AccountableOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_BoardId_Identifier",
                table: "CoreWorkTasks",
                columns: new[] { "BoardId", "Identifier" },
                unique: true,
                filter: "\"Identifier\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_BoardId_IdentifierSequence",
                table: "CoreWorkTasks",
                columns: new[] { "BoardId", "IdentifierSequence" },
                unique: true,
                filter: "\"IdentifierSequence\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CoreWorkTasks_Status",
                table: "CoreWorkTasks",
                sql: "\"Status\" IN ('Backlog', 'Ready', 'Assigned', 'Running', 'WaitingForApproval', 'Completed', 'Failed', 'Cancelled', 'Blocked')");

            migrationBuilder.CreateIndex(
                name: "IX_WorkExecutionAttempts_AgentWorkItemId",
                table: "WorkExecutionAttempts",
                column: "AgentWorkItemId",
                unique: true,
                filter: "\"AgentWorkItemId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkExecutionAttempts_StageExecutionId",
                table: "WorkExecutionAttempts",
                column: "StageExecutionId",
                unique: true,
                filter: "\"Status\" IN ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_WorkExecutionAttempts_StageExecutionId_Attempt",
                table: "WorkExecutionAttempts",
                columns: new[] { "StageExecutionId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemExecutions_SprintExecutionId_WorkItemId",
                table: "WorkItemExecutions",
                columns: new[] { "SprintExecutionId", "WorkItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemExecutions_WorkItemId",
                table: "WorkItemExecutions",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemStageAssignments_AgentInstallationId",
                table: "WorkItemStageAssignments",
                column: "AgentInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemStageAssignments_OrganizationUserId",
                table: "WorkItemStageAssignments",
                column: "OrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemStageAssignments_WorkItemId_StageKey",
                table: "WorkItemStageAssignments",
                columns: new[] { "WorkItemId", "StageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrchestrationEvents_BoardId_OccurredAt",
                table: "WorkOrchestrationEvents",
                columns: new[] { "BoardId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrchestrationEvents_SprintExecutionId_OccurredAt",
                table: "WorkOrchestrationEvents",
                columns: new[] { "SprintExecutionId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrchestrationPolicies_BoardId",
                table: "WorkOrchestrationPolicies",
                column: "BoardId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrchestrationPolicyRevisions_BoardId_IsPublished",
                table: "WorkOrchestrationPolicyRevisions",
                columns: new[] { "BoardId", "IsPublished" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrchestrationPolicyRevisions_PolicyId_Revision",
                table: "WorkOrchestrationPolicyRevisions",
                columns: new[] { "PolicyId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrchestrationStages_ColumnId",
                table: "WorkOrchestrationStages",
                column: "ColumnId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrchestrationStages_PolicyRevisionId_Key",
                table: "WorkOrchestrationStages",
                columns: new[] { "PolicyRevisionId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrchestrationTransitions_PolicyRevisionId_FromStageKey_~",
                table: "WorkOrchestrationTransitions",
                columns: new[] { "PolicyRevisionId", "FromStageKey", "OutcomeCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintExecutions_BoardId",
                table: "WorkSprintExecutions",
                column: "BoardId",
                unique: true,
                filter: "\"Status\" IN ('Active', 'Paused')");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintExecutions_PolicyRevisionId",
                table: "WorkSprintExecutions",
                column: "PolicyRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintExecutions_SprintId",
                table: "WorkSprintExecutions",
                column: "SprintId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkStageExecutions_ItemExecutionId_StageKey_Traversal",
                table: "WorkStageExecutions",
                columns: new[] { "ItemExecutionId", "StageKey", "Traversal" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_CoreOrganizationUsers_AccountableOrganization~",
                table: "CoreWorkTasks",
                column: "AccountableOrganizationUserId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkBoards_CoreOrganizationUsers_ManagerOrganizationUserId",
                table: "WorkBoards",
                column: "ManagerOrganizationUserId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_CoreOrganizationUsers_AccountableOrganization~",
                table: "CoreWorkTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkBoards_CoreOrganizationUsers_ManagerOrganizationUserId",
                table: "WorkBoards");

            migrationBuilder.DropTable(
                name: "WorkExecutionAttempts");

            migrationBuilder.DropTable(
                name: "WorkItemStageAssignments");

            migrationBuilder.DropTable(
                name: "WorkOrchestrationEvents");

            migrationBuilder.DropTable(
                name: "WorkOrchestrationStages");

            migrationBuilder.DropTable(
                name: "WorkOrchestrationTransitions");

            migrationBuilder.DropTable(
                name: "WorkStageExecutions");

            migrationBuilder.DropTable(
                name: "WorkItemExecutions");

            migrationBuilder.DropTable(
                name: "WorkSprintExecutions");

            migrationBuilder.DropTable(
                name: "WorkOrchestrationPolicyRevisions");

            migrationBuilder.DropTable(
                name: "WorkOrchestrationPolicies");

            migrationBuilder.DropIndex(
                name: "IX_WorkBoards_ManagerOrganizationUserId",
                table: "WorkBoards");

            migrationBuilder.DropIndex(
                name: "IX_WorkBoards_OrganizationId_Key",
                table: "WorkBoards");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_AccountableOrganizationUserId",
                table: "CoreWorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_BoardId_Identifier",
                table: "CoreWorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_BoardId_IdentifierSequence",
                table: "CoreWorkTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CoreWorkTasks_Status",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "Key",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "ManagerOrganizationUserId",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "NextItemSequence",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "AccountableOrganizationUserId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "Identifier",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "IdentifierSequence",
                table: "CoreWorkTasks");

            migrationBuilder.CreateTable(
                name: "WorkAutomationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AutomationIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConditionColumnId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    TargetColumnId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerEventType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAutomationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkAutomationRules_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkAutomationRules_WorkBoardColumns_ConditionColumnId",
                        column: x => x.ConditionColumnId,
                        principalTable: "WorkBoardColumns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAutomationRules_WorkBoardColumns_TargetColumnId",
                        column: x => x.TargetColumnId,
                        principalTable: "WorkBoardColumns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAutomationRules_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkDeliveryPipelines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActiveSprintId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActiveWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    BaseBranch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsecutiveInfrastructureFailures = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeveloperInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DevelopmentColumnId = table.Column<Guid>(type: "uuid", nullable: false),
                    DoneColumnId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MergeStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    MergeStrategy = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    QualityColumnId = table.Column<Guid>(type: "uuid", nullable: false),
                    QualityCycle = table.Column<int>(type: "integer", nullable: false),
                    QualityInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResumeAction = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SourceCommitSha = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SourcePullRequestUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
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
                name: "WorkAutomationExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizingGrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorizingGrantRevision = table.Column<long>(type: "bigint", nullable: true),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequiredAction = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SourceActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAutomationExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_ScopedActionGrants_AuthorizingGran~",
                        column: x => x.AuthorizingGrantId,
                        principalTable: "ScopedActionGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_WorkAutomationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "WorkAutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_WorkItemActivities_SourceActivityId",
                        column: x => x.SourceActivityId,
                        principalTable: "WorkItemActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_BoardId",
                table: "CoreWorkTasks",
                column: "BoardId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CoreWorkTasks_Status",
                table: "CoreWorkTasks",
                sql: "\"Status\" IN ('Backlog', 'Ready', 'Assigned', 'Running', 'WaitingForApproval', 'Completed', 'Failed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_AuthorizingGrantId",
                table: "WorkAutomationExecutions",
                column: "AuthorizingGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_BoardId_CompletedAt",
                table: "WorkAutomationExecutions",
                columns: new[] { "BoardId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_OrganizationId",
                table: "WorkAutomationExecutions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_RuleId_SourceActivityId",
                table: "WorkAutomationExecutions",
                columns: new[] { "RuleId", "SourceActivityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_SourceActivityId",
                table: "WorkAutomationExecutions",
                column: "SourceActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_AutomationIdentityId",
                table: "WorkAutomationRules",
                column: "AutomationIdentityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_BoardId_IsEnabled",
                table: "WorkAutomationRules",
                columns: new[] { "BoardId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_ConditionColumnId",
                table: "WorkAutomationRules",
                column: "ConditionColumnId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_OrganizationId",
                table: "WorkAutomationRules",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_TargetColumnId",
                table: "WorkAutomationRules",
                column: "TargetColumnId");

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
        }
    }
}
