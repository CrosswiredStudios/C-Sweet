using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSuggestedUserActionSupersession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAt",
                table: "SuggestedUserActions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByActionId",
                table: "SuggestedUserActions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupersededByRole",
                table: "SuggestedUserActions",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupersededAt",
                table: "SuggestedUserActions");

            migrationBuilder.DropColumn(
                name: "SupersededByActionId",
                table: "SuggestedUserActions");

            migrationBuilder.DropColumn(
                name: "SupersededByRole",
                table: "SuggestedUserActions");
        }
    }
}
