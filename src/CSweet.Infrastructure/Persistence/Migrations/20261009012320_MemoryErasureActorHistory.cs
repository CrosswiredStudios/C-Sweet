using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemoryErasureActorHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MemoryErasureReceipts_ActorHistory",
                table: "MemoryErasureReceipts",
                columns: new[] { "OrganizationId", "EmployeeId", "ActorApplicationUserId", "ActorOrganizationUserId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MemoryErasureReceipts_ActorHistory",
                table: "MemoryErasureReceipts");
        }
    }
}
