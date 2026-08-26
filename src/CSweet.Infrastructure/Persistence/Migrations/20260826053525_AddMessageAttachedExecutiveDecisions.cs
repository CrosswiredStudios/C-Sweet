using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageAttachedExecutiveDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ChatTurnId",
                table: "ExecutiveDecisions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationMessageId",
                table: "ExecutiveDecisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutiveDecisions_ConversationMessageId",
                table: "ExecutiveDecisions",
                column: "ConversationMessageId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExecutiveDecisions_CoreConversationMessages_ConversationMes~",
                table: "ExecutiveDecisions",
                column: "ConversationMessageId",
                principalTable: "CoreConversationMessages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExecutiveDecisions_CoreConversationMessages_ConversationMes~",
                table: "ExecutiveDecisions");

            migrationBuilder.DropIndex(
                name: "IX_ExecutiveDecisions_ConversationMessageId",
                table: "ExecutiveDecisions");

            migrationBuilder.DropColumn(
                name: "ConversationMessageId",
                table: "ExecutiveDecisions");

            migrationBuilder.AlterColumn<Guid>(
                name: "ChatTurnId",
                table: "ExecutiveDecisions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
