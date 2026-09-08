using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20260908151000_AllowCoordinationArtifactRevisions")]
public sealed class AllowCoordinationArtifactRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_AgentCoordinationTurns_SessionId_ArtifactType_ArtifactKey_A~", "AgentCoordinationTurns");
        migrationBuilder.CreateIndex("IX_AgentCoordinationTurns_SessionId_ArtifactType_ArtifactKey_A~",
            "AgentCoordinationTurns", new[] { "SessionId", "ArtifactType", "ArtifactKey", "ArtifactPageOrdinal" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // PostgreSQL rejects downgrade if revision history now violates the old constraint;
        // never delete recorded collaboration history to make a downgrade succeed.
        migrationBuilder.DropIndex("IX_AgentCoordinationTurns_SessionId_ArtifactType_ArtifactKey_A~", "AgentCoordinationTurns");
        migrationBuilder.CreateIndex("IX_AgentCoordinationTurns_SessionId_ArtifactType_ArtifactKey_A~",
            "AgentCoordinationTurns", new[] { "SessionId", "ArtifactType", "ArtifactKey", "ArtifactPageOrdinal" }, unique: true);
    }
}
