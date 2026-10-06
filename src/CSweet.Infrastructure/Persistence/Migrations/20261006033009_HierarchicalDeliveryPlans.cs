using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HierarchicalDeliveryPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ItemExecutionId",
                table: "WorkStageExecutions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "DeliveryExecutionId",
                table: "WorkStageExecutions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntegrationTargetBranch",
                table: "SourceControlWorkspaces",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "WorkDeliveryMutationReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDeliveryMutationReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkDeliveryPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ManagerOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ScopeRevision = table.Column<long>(type: "bigint", nullable: false),
                    EpicItemIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    BranchesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ScopesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDeliveryPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryPlans_CoreOrganizationUsers_ManagerOrganization~",
                        column: x => x.ManagerOrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryPlans_Workstreams_WorkstreamId",
                        column: x => x.WorkstreamId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkDeliveryExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeRevision = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentStageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CandidateJson = table.Column<string>(type: "jsonb", nullable: true),
                    AcceptanceJson = table.Column<string>(type: "jsonb", nullable: true),
                    BlockedReason = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDeliveryExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryExecutions_CoreWorkTasks_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryExecutions_WorkDeliveryPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "WorkDeliveryPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkTaskIntegrationReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeRevision = table.Column<long>(type: "bigint", nullable: false),
                    SourceCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CandidateCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTaskIntegrationReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkTaskIntegrationReceipts_WorkDeliveryPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "WorkDeliveryPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkTaskIntegrationReceipts_WorkItemExecutions_ItemExecutio~",
                        column: x => x.ItemExecutionId,
                        principalTable: "WorkItemExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkDeliveryPromotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MergeCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDeliveryPromotions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryPromotions_WorkDeliveryExecutions_ExecutionId",
                        column: x => x.ExecutionId,
                        principalTable: "WorkDeliveryExecutions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkStageExecutions_DeliveryExecutionId_StageKey_Traversal",
                table: "WorkStageExecutions",
                columns: new[] { "DeliveryExecutionId", "StageKey", "Traversal" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkStageExecution_OneOwner",
                table: "WorkStageExecutions",
                sql: "(\"ItemExecutionId\" IS NULL) <> (\"DeliveryExecutionId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryExecutions_PlanId_ScopeRevision_Scope_WorkItemId",
                table: "WorkDeliveryExecutions",
                columns: new[] { "PlanId", "ScopeRevision", "Scope", "WorkItemId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryExecutions_WorkItemId",
                table: "WorkDeliveryExecutions",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryMutationReceipts_OrganizationId_IdempotencyKey",
                table: "WorkDeliveryMutationReceipts",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryPlans_ManagerOrganizationUserId",
                table: "WorkDeliveryPlans",
                column: "ManagerOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryPlans_OrganizationId_WorkstreamId",
                table: "WorkDeliveryPlans",
                columns: new[] { "OrganizationId", "WorkstreamId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryPlans_WorkstreamId",
                table: "WorkDeliveryPlans",
                column: "WorkstreamId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryPromotions_ExecutionId_RepositoryId",
                table: "WorkDeliveryPromotions",
                columns: new[] { "ExecutionId", "RepositoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskIntegrationReceipts_ItemExecutionId",
                table: "WorkTaskIntegrationReceipts",
                column: "ItemExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskIntegrationReceipts_OrganizationId_WorkItemId_Creat~",
                table: "WorkTaskIntegrationReceipts",
                columns: new[] { "OrganizationId", "WorkItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskIntegrationReceipts_PlanId",
                table: "WorkTaskIntegrationReceipts",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskIntegrationReceipts_PublicationId",
                table: "WorkTaskIntegrationReceipts",
                column: "PublicationId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkStageExecutions_WorkDeliveryExecutions_DeliveryExecutio~",
                table: "WorkStageExecutions",
                column: "DeliveryExecutionId",
                principalTable: "WorkDeliveryExecutions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkStageExecutions_WorkDeliveryExecutions_DeliveryExecutio~",
                table: "WorkStageExecutions");

            migrationBuilder.DropTable(
                name: "WorkDeliveryMutationReceipts");

            migrationBuilder.DropTable(
                name: "WorkDeliveryPromotions");

            migrationBuilder.DropTable(
                name: "WorkTaskIntegrationReceipts");

            migrationBuilder.DropTable(
                name: "WorkDeliveryExecutions");

            migrationBuilder.DropTable(
                name: "WorkDeliveryPlans");

            migrationBuilder.DropIndex(
                name: "IX_WorkStageExecutions_DeliveryExecutionId_StageKey_Traversal",
                table: "WorkStageExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkStageExecution_OneOwner",
                table: "WorkStageExecutions");

            migrationBuilder.DropColumn(
                name: "DeliveryExecutionId",
                table: "WorkStageExecutions");

            migrationBuilder.DropColumn(
                name: "IntegrationTargetBranch",
                table: "SourceControlWorkspaces");

            migrationBuilder.AlterColumn<Guid>(
                name: "ItemExecutionId",
                table: "WorkStageExecutions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
