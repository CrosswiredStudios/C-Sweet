using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkBoardWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "WorkBoards",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "BoardColumnId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BoardRank",
                table: "CoreWorkTasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "CoreWorkTasks",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Task");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentWorkTaskId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "CoreWorkTasks",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_BoardColumnId_BoardRank",
                table: "CoreWorkTasks",
                columns: new[] { "BoardColumnId", "BoardRank" });

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_ParentWorkTaskId",
                table: "CoreWorkTasks",
                column: "ParentWorkTaskId");

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_CoreWorkTasks_ParentWorkTaskId",
                table: "CoreWorkTasks",
                column: "ParentWorkTaskId",
                principalTable: "CoreWorkTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_WorkBoardColumns_BoardColumnId",
                table: "CoreWorkTasks",
                column: "BoardColumnId",
                principalTable: "WorkBoardColumns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_CoreWorkTasks_ParentWorkTaskId",
                table: "CoreWorkTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_WorkBoardColumns_BoardColumnId",
                table: "CoreWorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_BoardColumnId_BoardRank",
                table: "CoreWorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_ParentWorkTaskId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "BoardColumnId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "BoardRank",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "ParentWorkTaskId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "CoreWorkTasks");
        }
    }
}
