using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHiringRecommendationFulfillments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FulfilledHeadcount",
                table: "WorkforcePlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "HiringRecommendationFulfillments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkforcePlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffingActionProposalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiringRecommendationFulfillments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HiringRecommendationFulfillments_CoreOrganizationUsers_Resu~",
                        column: x => x.ResultOrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HiringRecommendationFulfillments_StaffingActionProposals_St~",
                        column: x => x.StaffingActionProposalId,
                        principalTable: "StaffingActionProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HiringRecommendationFulfillments_WorkforcePlans_WorkforcePl~",
                        column: x => x.WorkforcePlanId,
                        principalTable: "WorkforcePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HiringRecommendationFulfillments_ResultOrganizationUserId",
                table: "HiringRecommendationFulfillments",
                column: "ResultOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HiringRecommendationFulfillments_StaffingActionProposalId",
                table: "HiringRecommendationFulfillments",
                column: "StaffingActionProposalId",
                unique: true,
                filter: "\"StaffingActionProposalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HiringRecommendationFulfillments_WorkforcePlanId_Idempotenc~",
                table: "HiringRecommendationFulfillments",
                columns: new[] { "WorkforcePlanId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HiringRecommendationFulfillments_WorkforcePlanId_ResultOrga~",
                table: "HiringRecommendationFulfillments",
                columns: new[] { "WorkforcePlanId", "ResultOrganizationUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HiringRecommendationFulfillments");

            migrationBuilder.DropColumn(
                name: "FulfilledHeadcount",
                table: "WorkforcePlans");
        }
    }
}
