using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

/// <summary>
/// Agents installed before posture governance acted without per-write approval. Pin those
/// installations to Auto so upgrading does not start queueing an approval for every write;
/// installations created afterwards keep the Manual product default.
/// </summary>
[DbContext(typeof(CSweetDbContext))]
[Migration("20261105090000_PreserveExistingAgentAutonomy")]
public sealed class PreserveExistingAgentAutonomy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE "AgentInstallations"
        SET "Posture" = 'Auto', "GovernanceRevision" = "GovernanceRevision" + 1
        WHERE "Posture" IS NULL AND "PostureChangedAt" IS NULL;
        """);

    // Rows a person has not touched since (no change timestamp) return to the default.
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        UPDATE "AgentInstallations"
        SET "Posture" = NULL, "GovernanceRevision" = "GovernanceRevision" + 1
        WHERE "Posture" = 'Auto' AND "PostureChangedAt" IS NULL;
        """);
}
