using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintPlanningMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CapacityPoints",
                table: "WorkSprints",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatePoints",
                table: "CoreWorkTasks",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkSprintSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    SprintId = table.Column<Guid>(type: "uuid", nullable: false),
                    SprintName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Goal = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CapacityPoints = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    CommittedItemCount = table.Column<int>(type: "integer", nullable: false),
                    CompletedItemCount = table.Column<int>(type: "integer", nullable: false),
                    CommittedPoints = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    CompletedPoints = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    ScopeJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSprintSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkSprintSnapshots_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkSprintSnapshots_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkSprintSnapshots_WorkSprints_SprintId",
                        column: x => x.SprintId,
                        principalTable: "WorkSprints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintSnapshots_BoardId_CompletedAt",
                table: "WorkSprintSnapshots",
                columns: new[] { "BoardId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintSnapshots_OrganizationId",
                table: "WorkSprintSnapshots",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintSnapshots_SprintId",
                table: "WorkSprintSnapshots",
                column: "SprintId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkSprintSnapshots");

            migrationBuilder.DropColumn(
                name: "CapacityPoints",
                table: "WorkSprints");

            migrationBuilder.DropColumn(
                name: "EstimatePoints",
                table: "CoreWorkTasks");
        }
    }
}
