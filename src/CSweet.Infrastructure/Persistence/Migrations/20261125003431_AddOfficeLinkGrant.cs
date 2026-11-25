using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfficeLinkGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OfficeLinkId",
                table: "ExecutionNodeEnrollments",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SealedEnrollmentToken",
                table: "ExecutionNodeEnrollments",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionNodeEnrollments_OfficeLinkId",
                table: "ExecutionNodeEnrollments",
                column: "OfficeLinkId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExecutionNodeEnrollments_OfficeLinkId",
                table: "ExecutionNodeEnrollments");

            migrationBuilder.DropColumn(
                name: "OfficeLinkId",
                table: "ExecutionNodeEnrollments");

            migrationBuilder.DropColumn(
                name: "SealedEnrollmentToken",
                table: "ExecutionNodeEnrollments");
        }
    }
}
