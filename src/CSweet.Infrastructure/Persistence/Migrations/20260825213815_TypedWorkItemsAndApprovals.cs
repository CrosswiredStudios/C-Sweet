using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TypedWorkItemsAndApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProfileKey",
                table: "WorkBoards",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "general-work.v1");

            migrationBuilder.AddColumn<long>(
                name: "PlanningRevision",
                table: "CoreWorkTasks",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "ProposalArtifactDigest",
                table: "CoreWorkTasks",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProposalCoordinationSessionId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposalItemKey",
                table: "CoreWorkTasks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TypeKey",
                table: "CoreWorkTasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "general.task.v1");

            migrationBuilder.Sql("""
                UPDATE "CoreWorkTasks"
                SET "TypeKey" = CASE "Kind"
                    WHEN 'Initiative' THEN 'general.initiative.v1'
                    WHEN 'Epic' THEN 'general.epic.v1'
                    WHEN 'Story' THEN 'general.story.v1'
                    ELSE 'general.task.v1'
                END,
                "PlanningRevision" = 1;
                """);

            migrationBuilder.CreateTable(
                name: "WorkItemApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PlanningRevision = table.Column<long>(type: "bigint", nullable: false),
                    ApproverEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApproverInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequiredRoleCategory = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ArtifactDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CoordinationSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Rationale = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    ManagerWaiverSource = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemApprovals_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkItemApprovals_CoreWorkTasks_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_BoardId_TypeKey_PlanningRevision",
                table: "CoreWorkTasks",
                columns: new[] { "BoardId", "TypeKey", "PlanningRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemApprovals_BoardId_Status_PolicyKey",
                table: "WorkItemApprovals",
                columns: new[] { "BoardId", "Status", "PolicyKey" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemApprovals_OrganizationId",
                table: "WorkItemApprovals",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemApprovals_WorkItemId_PolicyKey",
                table: "WorkItemApprovals",
                columns: new[] { "WorkItemId", "PolicyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemApprovals");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_BoardId_TypeKey_PlanningRevision",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "ProfileKey",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "PlanningRevision",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "ProposalArtifactDigest",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "ProposalCoordinationSessionId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "ProposalItemKey",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "TypeKey",
                table: "CoreWorkTasks");
        }
    }
}
