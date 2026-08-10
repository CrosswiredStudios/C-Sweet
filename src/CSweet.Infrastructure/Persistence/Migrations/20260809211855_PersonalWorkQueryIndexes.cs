using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersonalWorkQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_BoardId_ArchivedAt_Status_BoardRank",
                table: "CoreWorkTasks",
                columns: new[] { "BoardId", "ArchivedAt", "Status", "BoardRank" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_BoardId_ArchivedAt_Status_BoardRank",
                table: "CoreWorkTasks");
        }
    }
}
