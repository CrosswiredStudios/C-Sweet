using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveGenerationTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GenerationIsEstimated",
                table: "AgentRunLogs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GenerationObservedAt",
                table: "AgentRunLogs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GenerationOutputTokens",
                table: "AgentRunLogs",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GenerationStartedAt",
                table: "AgentRunLogs",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenerationIsEstimated",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "GenerationObservedAt",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "GenerationOutputTokens",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "GenerationStartedAt",
                table: "AgentRunLogs");
        }
    }
}
