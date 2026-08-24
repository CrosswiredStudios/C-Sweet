using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArchitectWorkCoordination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtifactDigest",
                table: "WorkItemComments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CausationId",
                table: "WorkItemComments",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CoordinationSessionId",
                table: "WorkItemComments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "WorkItemComments",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceMessageId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceConversationId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceChatTurnId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "MaximumTurns",
                table: "AgentCoordinationSessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceAssignmentRevision",
                table: "AgentCoordinationSessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceBoardId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "AgentCoordinationSessions",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Chat");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceSprintExecutionId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceStageExecutionId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceWorkItemId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemComments_CoordinationSessionId_Kind",
                table: "WorkItemComments",
                columns: new[] { "CoordinationSessionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_OrganizationId_SourceWorkItemId_S~",
                table: "AgentCoordinationSessions",
                columns: new[] { "OrganizationId", "SourceWorkItemId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItemComments_CoordinationSessionId_Kind",
                table: "WorkItemComments");

            migrationBuilder.DropIndex(
                name: "IX_AgentCoordinationSessions_OrganizationId_SourceWorkItemId_S~",
                table: "AgentCoordinationSessions");

            migrationBuilder.DropColumn(
                name: "ArtifactDigest",
                table: "WorkItemComments");

            migrationBuilder.DropColumn(
                name: "CausationId",
                table: "WorkItemComments");

            migrationBuilder.DropColumn(
                name: "CoordinationSessionId",
                table: "WorkItemComments");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "WorkItemComments");

            migrationBuilder.DropColumn(
                name: "MaximumTurns",
                table: "AgentCoordinationSessions");

            migrationBuilder.DropColumn(
                name: "SourceAssignmentRevision",
                table: "AgentCoordinationSessions");

            migrationBuilder.DropColumn(
                name: "SourceBoardId",
                table: "AgentCoordinationSessions");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "AgentCoordinationSessions");

            migrationBuilder.DropColumn(
                name: "SourceSprintExecutionId",
                table: "AgentCoordinationSessions");

            migrationBuilder.DropColumn(
                name: "SourceStageExecutionId",
                table: "AgentCoordinationSessions");

            migrationBuilder.DropColumn(
                name: "SourceWorkItemId",
                table: "AgentCoordinationSessions");

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceMessageId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceConversationId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceChatTurnId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
