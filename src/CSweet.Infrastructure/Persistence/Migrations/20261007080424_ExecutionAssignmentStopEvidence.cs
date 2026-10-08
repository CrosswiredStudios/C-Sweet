using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExecutionAssignmentStopEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StopEvidenceVersion",
                table: "ExecutionWorkloadAssignments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ExecutionAssignmentAttempts",
                columns: table => new
                {
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FencingEpoch = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderInstanceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NeverCreated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionAssignmentAttempts", x => new { x.AssignmentId, x.FencingEpoch });
                    table.ForeignKey(
                        name: "FK_ExecutionAssignmentAttempts_ExecutionNodes_ExecutionNodeId",
                        column: x => x.ExecutionNodeId,
                        principalTable: "ExecutionNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExecutionAssignmentAttempts_ExecutionWorkloadAssignments_As~",
                        column: x => x.AssignmentId,
                        principalTable: "ExecutionWorkloadAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionAssignmentAttempts_ExecutionNodeId",
                table: "ExecutionAssignmentAttempts",
                column: "ExecutionNodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "ExecutionAssignmentAttempts") THEN
                        RAISE EXCEPTION 'execution_stop_evidence_downgrade_requires_snapshot';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "ExecutionAssignmentAttempts");

            migrationBuilder.DropColumn(
                name: "StopEvidenceVersion",
                table: "ExecutionWorkloadAssignments");
        }
    }
}
