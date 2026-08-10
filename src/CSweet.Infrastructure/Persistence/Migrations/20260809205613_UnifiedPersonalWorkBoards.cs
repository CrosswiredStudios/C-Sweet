using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnifiedPersonalWorkBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkBoards_CoreOrganizationUsers_PersonalTodoOwnerOrganizat~",
                table: "WorkBoards");

            migrationBuilder.DropIndex(
                name: "IX_WorkBoards_PersonalTodoOwnerOrganizationUserId",
                table: "WorkBoards");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_CreatedByOrganizationUserId_PersonalTodoIdemp~",
                table: "CoreWorkTasks");

            migrationBuilder.RenameColumn(
                name: "PersonalTodoOwnerOrganizationUserId",
                table: "WorkBoards",
                newName: "OwnerOrganizationUserId");

            migrationBuilder.RenameColumn(
                name: "PersonalTodoResultSummary",
                table: "CoreWorkTasks",
                newName: "ResultSummary");

            migrationBuilder.RenameColumn(
                name: "PersonalTodoIdempotencyKey",
                table: "CoreWorkTasks",
                newName: "CreationIdempotencyKey");

            migrationBuilder.RenameColumn(
                name: "PersonalTodoClaimExpiresAt",
                table: "CoreWorkTasks",
                newName: "ClaimExpiresAt");

            migrationBuilder.RenameColumn(
                name: "PersonalTodoClaimEventId",
                table: "CoreWorkTasks",
                newName: "ClaimEventId");

            migrationBuilder.RenameColumn(
                name: "PersonalTodoBlockReason",
                table: "CoreWorkTasks",
                newName: "BlockReason");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "WorkBoards",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.Sql("""
                UPDATE "WorkBoards"
                SET "Kind" = CASE WHEN "IsPersonalTodo" THEN 'Personal' ELSE 'Standard' END;

                UPDATE "WorkBoardColumns" AS columns
                SET "Position" = 3
                FROM "WorkBoards" AS boards
                WHERE columns."BoardId" = boards."Id"
                  AND boards."IsPersonalTodo" = TRUE
                  AND columns."Category" = 'Done'
                  AND NOT EXISTS (
                      SELECT 1 FROM "WorkBoardColumns" existing
                      WHERE existing."BoardId" = boards."Id" AND existing."Position" = 3);

                INSERT INTO "WorkBoardColumns" ("Id", "BoardId", "Name", "Category", "Position", "WipPolicy", "WipLimit")
                SELECT md5(boards."Id"::text || ':blocked')::uuid, boards."Id", 'Blocked', 'Blocked', 2, 'Disabled', NULL
                FROM "WorkBoards" AS boards
                WHERE boards."IsPersonalTodo" = TRUE
                  AND NOT EXISTS (
                      SELECT 1 FROM "WorkBoardColumns" existing
                      WHERE existing."BoardId" = boards."Id" AND existing."Category" = 'Blocked');
                """);

            migrationBuilder.DropColumn(
                name: "IsPersonalTodo",
                table: "WorkBoards");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "CoreWorkTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CausationId",
                table: "CoreWorkTasks",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "CoreWorkTasks",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "CoreOrganizationUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoards_OwnerOrganizationUserId",
                table: "WorkBoards",
                column: "OwnerOrganizationUserId",
                unique: true,
                filter: "\"OwnerOrganizationUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_CreatedByOrganizationUserId_CreationIdempoten~",
                table: "CoreWorkTasks",
                columns: new[] { "CreatedByOrganizationUserId", "CreationIdempotencyKey" },
                unique: true,
                filter: "\"CreationIdempotencyKey\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkBoards_CoreOrganizationUsers_OwnerOrganizationUserId",
                table: "WorkBoards",
                column: "OwnerOrganizationUserId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkBoards_CoreOrganizationUsers_OwnerOrganizationUserId",
                table: "WorkBoards");

            migrationBuilder.DropIndex(
                name: "IX_WorkBoards_OwnerOrganizationUserId",
                table: "WorkBoards");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_CreatedByOrganizationUserId_CreationIdempoten~",
                table: "CoreWorkTasks");

            migrationBuilder.AddColumn<bool>(
                name: "IsPersonalTodo",
                table: "WorkBoards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "WorkBoards" SET "IsPersonalTodo" = TRUE WHERE "Kind" = 'Personal';
                DELETE FROM "WorkBoardColumns" AS columns
                USING "WorkBoards" AS boards
                WHERE columns."BoardId" = boards."Id"
                  AND boards."Kind" = 'Personal'
                  AND columns."Id" = md5(boards."Id"::text || ':blocked')::uuid;
                UPDATE "WorkBoardColumns" AS columns
                SET "Position" = 2
                FROM "WorkBoards" AS boards
                WHERE columns."BoardId" = boards."Id"
                  AND boards."Kind" = 'Personal'
                  AND columns."Category" = 'Done'
                  AND NOT EXISTS (
                      SELECT 1 FROM "WorkBoardColumns" existing
                      WHERE existing."BoardId" = boards."Id" AND existing."Position" = 2
                        AND existing."Id" <> columns."Id");
                """);

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "CausationId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "CoreOrganizationUsers");

            migrationBuilder.RenameColumn(
                name: "OwnerOrganizationUserId",
                table: "WorkBoards",
                newName: "PersonalTodoOwnerOrganizationUserId");

            migrationBuilder.RenameColumn(
                name: "ResultSummary",
                table: "CoreWorkTasks",
                newName: "PersonalTodoResultSummary");

            migrationBuilder.RenameColumn(
                name: "CreationIdempotencyKey",
                table: "CoreWorkTasks",
                newName: "PersonalTodoIdempotencyKey");

            migrationBuilder.RenameColumn(
                name: "ClaimExpiresAt",
                table: "CoreWorkTasks",
                newName: "PersonalTodoClaimExpiresAt");

            migrationBuilder.RenameColumn(
                name: "ClaimEventId",
                table: "CoreWorkTasks",
                newName: "PersonalTodoClaimEventId");

            migrationBuilder.RenameColumn(
                name: "BlockReason",
                table: "CoreWorkTasks",
                newName: "PersonalTodoBlockReason");

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoards_PersonalTodoOwnerOrganizationUserId",
                table: "WorkBoards",
                column: "PersonalTodoOwnerOrganizationUserId",
                unique: true,
                filter: "\"PersonalTodoOwnerOrganizationUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_CreatedByOrganizationUserId_PersonalTodoIdemp~",
                table: "CoreWorkTasks",
                columns: new[] { "CreatedByOrganizationUserId", "PersonalTodoIdempotencyKey" },
                unique: true,
                filter: "\"PersonalTodoIdempotencyKey\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkBoards_CoreOrganizationUsers_PersonalTodoOwnerOrganizat~",
                table: "WorkBoards",
                column: "PersonalTodoOwnerOrganizationUserId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
