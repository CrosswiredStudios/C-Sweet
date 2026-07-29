using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintTimeSeriesMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkSprintMetricPoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SprintId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ScopeItemCount = table.Column<int>(type: "integer", nullable: false),
                    CompletedItemCount = table.Column<int>(type: "integer", nullable: false),
                    ScopePoints = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    CompletedPoints = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    RemainingPoints = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSprintMetricPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkSprintMetricPoints_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkSprintMetricPoints_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkSprintMetricPoints_WorkSprints_SprintId",
                        column: x => x.SprintId,
                        principalTable: "WorkSprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintMetricPoints_BoardId_OccurredAt",
                table: "WorkSprintMetricPoints",
                columns: new[] { "BoardId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintMetricPoints_OrganizationId",
                table: "WorkSprintMetricPoints",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintMetricPoints_SprintId_OccurredAt",
                table: "WorkSprintMetricPoints",
                columns: new[] { "SprintId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkSprintMetricPoints");
        }
    }
}
