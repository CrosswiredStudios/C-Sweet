using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableAgentHireOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentHireOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentDefinitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentHireOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentHireOperations_AgentDefinitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "AgentDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentHireOperations_StaffingActionProposals_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "StaffingActionProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentHireOperations_AgentDefinitionId",
                table: "AgentHireOperations",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentHireOperations_InitiatedByOrganizationUserId_Dismissed~",
                table: "AgentHireOperations",
                columns: new[] { "InitiatedByOrganizationUserId", "DismissedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentHireOperations_Status_LeaseUntil",
                table: "AgentHireOperations",
                columns: new[] { "Status", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentHireOperations_WorkflowId",
                table: "AgentHireOperations",
                column: "WorkflowId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentHireOperations");
        }
    }
}
