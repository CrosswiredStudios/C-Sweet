using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnboundPersonalWorkContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(name: "Title", table: "CoreWorkTasks", type: "text", nullable: false,
                oldClrType: typeof(string), oldType: "character varying(512)", oldMaxLength: 512);

            migrationBuilder.AlterColumn<string>(
                name: "ResultSummary",
                table: "CoreWorkTasks",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4096)",
                oldMaxLength: 4096,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(name: "Title", table: "CoreWorkTasks", type: "character varying(512)", maxLength: 512,
                nullable: false, oldClrType: typeof(string), oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "ResultSummary",
                table: "CoreWorkTasks",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
