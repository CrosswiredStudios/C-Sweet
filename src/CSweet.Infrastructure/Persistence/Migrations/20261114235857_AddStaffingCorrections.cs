using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffingCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CorrectionId",
                table: "ResourceChangeRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StaffingCorrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    RequestedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReplacementRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffingCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffingCorrections_ResourceChangeRequests_SourceRequestId",
                        column: x => x.SourceRequestId,
                        principalTable: "ResourceChangeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffingCorrections_WorkforcePlans_RecommendationId",
                        column: x => x.RecommendationId,
                        principalTable: "WorkforcePlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffingCorrections_OrganizationId_IdempotencyKey",
                table: "StaffingCorrections",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffingCorrections_OrganizationId_SourceRequestId_RoleKey_~",
                table: "StaffingCorrections",
                columns: new[] { "OrganizationId", "SourceRequestId", "RoleKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffingCorrections_RecommendationId",
                table: "StaffingCorrections",
                column: "RecommendationId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffingCorrections_SourceRequestId",
                table: "StaffingCorrections",
                column: "SourceRequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffingCorrections");

            migrationBuilder.DropColumn(
                name: "CorrectionId",
                table: "ResourceChangeRequests");
        }
    }
}
