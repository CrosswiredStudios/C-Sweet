using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HierarchicalDeliveryFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkDeliveryFinding",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FindingDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RemediationTaskIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ResolutionEvidence = table.Column<string>(type: "text", nullable: true),
                    ResolvedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDeliveryFinding", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkDeliveryFinding_WorkDeliveryPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "WorkDeliveryPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryFinding_ExecutionId_CandidateDigest_FindingDige~",
                table: "WorkDeliveryFinding",
                columns: new[] { "ExecutionId", "CandidateDigest", "FindingDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkDeliveryFinding_PlanId",
                table: "WorkDeliveryFinding",
                column: "PlanId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkDeliveryFinding");
        }
    }
}
