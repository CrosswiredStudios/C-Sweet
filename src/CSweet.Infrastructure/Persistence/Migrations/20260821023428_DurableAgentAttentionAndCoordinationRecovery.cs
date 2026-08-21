using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableAgentAttentionAndCoordinationRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextReviewAt",
                table: "CoreWorkTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WaitingOnOrganizationUserId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaitingReason",
                table: "CoreWorkTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttentionReviewAt",
                table: "AgentSchedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttentionReviewAt",
                table: "AgentSchedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastResumeIdempotencyKey",
                table: "AgentCoordinationSessions",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextReviewAt",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "WaitingOnOrganizationUserId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "WaitingReason",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "LastAttentionReviewAt",
                table: "AgentSchedules");

            migrationBuilder.DropColumn(
                name: "NextAttentionReviewAt",
                table: "AgentSchedules");

            migrationBuilder.DropColumn(
                name: "LastResumeIdempotencyKey",
                table: "AgentCoordinationSessions");
        }
    }
}
