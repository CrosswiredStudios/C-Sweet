using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleCategoriesAndSpecializations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreferredSpecializationKeysJson",
                table: "ResourceChangeRoles",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "RoleCategoryKey",
                table: "ResourceChangeRoles",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreferredSpecializationKeysJson",
                table: "ResourceChangeRoles");

            migrationBuilder.DropColumn(
                name: "RoleCategoryKey",
                table: "ResourceChangeRoles");
        }
    }
}
