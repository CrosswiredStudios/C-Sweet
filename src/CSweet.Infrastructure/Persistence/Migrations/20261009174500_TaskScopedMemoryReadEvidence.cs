using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

public partial class TaskScopedMemoryReadEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex(
        name: "IX_AgentMemoryReadReceipts_RuntimeId_WorkId_Attempt",
        table: "AgentMemoryReadReceipts", columns: new[] { "RuntimeId", "WorkId", "Attempt" });

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex(
        name: "IX_AgentMemoryReadReceipts_RuntimeId_WorkId_Attempt", table: "AgentMemoryReadReceipts");
}
