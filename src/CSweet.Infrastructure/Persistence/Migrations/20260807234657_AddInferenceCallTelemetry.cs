using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInferenceCallTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChatTurnId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvocationKind",
                table: "AgentRunLogs",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<int>(
                name: "InvocationSequence",
                table: "AgentRunLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptInstructionCharacters",
                table: "AgentRunLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptMemoryCharacters",
                table: "AgentRunLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptMessageCharacters",
                table: "AgentRunLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptToolCharacters",
                table: "AgentRunLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokenCachedInputCount",
                table: "AgentRunLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokenReasoningCount",
                table: "AgentRunLogs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsageAdditionalCountsJson",
                table: "AgentRunLogs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunLogs_ChatTurnId_InvocationSequence",
                table: "AgentRunLogs",
                columns: new[] { "ChatTurnId", "InvocationSequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRunLogs_ChatTurnId_InvocationSequence",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "ChatTurnId",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "InvocationKind",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "InvocationSequence",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "PromptInstructionCharacters",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "PromptMemoryCharacters",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "PromptMessageCharacters",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "PromptToolCharacters",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "TokenCachedInputCount",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "TokenReasoningCount",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "UsageAdditionalCountsJson",
                table: "AgentRunLogs");
        }
    }
}
