using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20260904050000_BackfillChiefExecutiveOfficerLeadership")]
public sealed class BackfillChiefExecutiveOfficerLeadership : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            INSERT INTO "LeadershipAssignments" ("Id", "OrganizationId", "OrganizationUserId", "PositionKey", "StartsAt", "EndsAt")
            SELECT gen_random_uuid(), owner."OrganizationId", owner."Id", 'chief-executive-officer', NOW(), NULL
            FROM (
                SELECT DISTINCT ON (u."OrganizationId") u."OrganizationId", u."Id"
                FROM "CoreOrganizationUsers" u
                WHERE u."IsActive" = TRUE AND u."PermissionLevel" = 'Owner'
                ORDER BY u."OrganizationId", u."CreatedAt", u."Id"
            ) owner
            WHERE NOT EXISTS (
                SELECT 1 FROM "LeadershipAssignments" existing
                WHERE existing."OrganizationId" = owner."OrganizationId"
                  AND existing."PositionKey" = 'chief-executive-officer'
                  AND existing."EndsAt" IS NULL
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally irreversible: rolling back must not erase organization leadership.
    }
}
