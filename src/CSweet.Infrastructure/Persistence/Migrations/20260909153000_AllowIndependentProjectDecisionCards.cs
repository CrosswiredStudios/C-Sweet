using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20260909153000_AllowIndependentProjectDecisionCards")]
public sealed class AllowIndependentProjectDecisionCards : Migration
{
    private const string Index = "IX_ExecutiveDecisions_ConversationId_RequestingInstallationId_~";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(Index, "ExecutiveDecisions");
        migrationBuilder.CreateIndex(Index, "ExecutiveDecisions",
            new[] { "ConversationId", "RequestingInstallationId", "Status" }, unique: true,
            filter: "\"Status\" = 'Pending' AND (\"OptionsJson\" ->> 'workstreamDecisionId') IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Refuse an incompatible downgrade instead of deleting outstanding questions.
        migrationBuilder.DropIndex(Index, "ExecutiveDecisions");
        migrationBuilder.CreateIndex(Index, "ExecutiveDecisions",
            new[] { "ConversationId", "RequestingInstallationId", "Status" }, unique: true,
            filter: "\"Status\" = 'Pending'");
    }
}
