using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LegacyProjectComputeRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegacyProjectComputeAuthorizations",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyProjectComputeAuthorizations", x => x.EnvironmentId);
                });
            migrationBuilder.Sql("""
                INSERT INTO "LegacyProjectComputeAuthorizations" ("EnvironmentId", "OrganizationId", "InstallationId")
                SELECT c."Id", c."OrganizationId", c."InstallationId" FROM "ComputeEnvironments" c
                WHERE c."TeardownConfirmedAt" IS NULL AND EXISTS (
                    SELECT 1 FROM "LegacyDevelopmentAuthorizations" a JOIN "CoreWorkTasks" t ON t."Id" = a."WorkItemId"
                    WHERE a."OrganizationId" = c."OrganizationId" AND t."AssignedAgentInstallationId" = c."InstallationId"
                        AND t."Status" NOT IN ('Completed', 'Cancelled') AND t."ArchivedAt" IS NULL
                ) ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LegacyProjectComputeAuthorizations");
        }
    }
}
