using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskExecutionTiming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "WorkLifecycleEvents",
                type: "text",
                nullable: false,
                defaultValue: "StatusTransition");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastConfirmedAt",
                table: "AgentWorkAttempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgentWorkAttemptId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkExecutionContexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkExecutionContexts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkExecutionIntervals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentWorkAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: true),
                    AncestorWorkItemIdsJson = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedThrough = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "text", nullable: true),
                    Provenance = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkExecutionIntervals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkExecutionContexts_AgentWorkItemId",
                table: "WorkExecutionContexts",
                column: "AgentWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkExecutionContexts_OrganizationId_RootWorkItemId",
                table: "WorkExecutionContexts",
                columns: new[] { "OrganizationId", "RootWorkItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkExecutionIntervals_AgentWorkAttemptId",
                table: "WorkExecutionIntervals",
                column: "AgentWorkAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkExecutionIntervals_OrganizationId_StartedAt",
                table: "WorkExecutionIntervals",
                columns: new[] { "OrganizationId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkExecutionContexts");

            migrationBuilder.DropTable(
                name: "WorkExecutionIntervals");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "WorkLifecycleEvents");

            migrationBuilder.DropColumn(
                name: "LastConfirmedAt",
                table: "AgentWorkAttempts");

            migrationBuilder.DropColumn(
                name: "AgentWorkAttemptId",
                table: "AgentRunLogs");
        }
    }
}
