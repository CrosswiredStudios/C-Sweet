using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteSuggestedHiringActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "SuggestedUserActions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResultOrganizationUserId",
                table: "SuggestedUserActions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedUserActions_ResultOrganizationUserId",
                table: "SuggestedUserActions",
                column: "ResultOrganizationUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_SuggestedUserActions_CoreOrganizationUsers_ResultOrganizati~",
                table: "SuggestedUserActions",
                column: "ResultOrganizationUserId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SuggestedUserActions_CoreOrganizationUsers_ResultOrganizati~",
                table: "SuggestedUserActions");

            migrationBuilder.DropIndex(
                name: "IX_SuggestedUserActions_ResultOrganizationUserId",
                table: "SuggestedUserActions");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "SuggestedUserActions");

            migrationBuilder.DropColumn(
                name: "ResultOrganizationUserId",
                table: "SuggestedUserActions");
        }
    }
}
