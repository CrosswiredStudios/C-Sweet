using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillApprovedTeamAgentGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "ScopedActionGrants" (
                    "Id", "OrganizationId", "SubjectKind", "SubjectId", "Action",
                    "ScopeKind", "ScopeId", "CanDelegate", "ParentGrantId",
                    "GrantedBySubjectKind", "GrantedBySubjectId", "Revision",
                    "GrantedAt", "ExpiresAt", "RevokedAt")
                SELECT
                    md5(ai."Id"::text || ':' || tm."TeamId"::text || ':' || requirement.value->>'name')::uuid,
                    tm."OrganizationId",
                    'AgentInstallation',
                    ai."Id",
                    requirement.value->>'name',
                    'Team',
                    tm."TeamId",
                    FALSE,
                    NULL,
                    'OrganizationUser',
                    team."LeadOrganizationUserId",
                    1,
                    aig."ApprovedAt",
                    NULL,
                    NULL
                FROM "TeamMemberships" tm
                INNER JOIN "OrganizationTeams" team ON team."Id" = tm."TeamId"
                INNER JOIN "CoreOrganizationUsers" employee
                    ON employee."Id" = tm."OrganizationUserId"
                INNER JOIN "AgentInstallations" ai
                    ON ai."Id" = employee."AgentInstallationId"
                INNER JOIN "AgentInstallationGrants" aig
                    ON aig."AgentInstallationId" = ai."Id"
                INNER JOIN "AgentPackageVersions" package
                    ON package."Id" = ai."PackageVersionId"
                CROSS JOIN LATERAL jsonb_array_elements(
                    COALESCE((package."ManifestJson"::jsonb)->'requires', '[]'::jsonb)) requirement(value)
                WHERE tm."EndedAt" IS NULL
                  AND team."ArchivedAt" IS NULL
                  AND employee."IsActive"
                  AND ai."IsEnabled"
                  AND ai."RevisionStatus" = 'Active'
                  AND ai."Scope" = 'Organization'
                  AND ai."BusinessId" = tm."OrganizationId"::text
                  AND requirement.value->>'scope' IN ('team', 'board')
                  AND aig."RequiredCapabilitiesJson"::jsonb ? (requirement.value->>'name')
                  AND requirement.value->>'name' IN (
                      'work.board.read',
                      'work.board.create',
                      'work.board.columns.configure',
                      'work.item.read',
                      'work.item.create',
                      'work.item.comment',
                      'work.item.estimate',
                      'work.item.move',
                      'work.item.transfer',
                      'work.sprint.read',
                      'work.sprint.create',
                      'work.sprint.scope.manage',
                      'work.sprint.capacity.manage',
                      'work.sprint.carryover',
                      'work.sprint.report.read',
                      'work.orchestration.read',
                      'work.orchestration.preflight',
                      'work.orchestration.start',
                      'work.orchestration.pause',
                      'work.orchestration.resume',
                      'work.orchestration.cancel',
                      'work.orchestration.retry',
                      'work.orchestration.software-template.configure',
                      'git.repository.team-options.v1')
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "ScopedActionGrants" existing
                      WHERE existing."OrganizationId" = tm."OrganizationId"
                        AND existing."SubjectKind" = 'AgentInstallation'
                        AND existing."SubjectId" = ai."Id"
                        AND existing."Action" = requirement.value->>'name'
                        AND existing."ScopeKind" = 'Team'
                        AND existing."ScopeId" = tm."TeamId"
                        AND existing."RevokedAt" IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "ScopedActionGrants" grant_row
                USING "TeamMemberships" tm,
                      "CoreOrganizationUsers" employee,
                      "AgentInstallations" ai,
                      "AgentPackageVersions" package,
                      LATERAL jsonb_array_elements(
                          COALESCE((package."ManifestJson"::jsonb)->'requires', '[]'::jsonb)) requirement(value)
                WHERE employee."Id" = tm."OrganizationUserId"
                  AND ai."Id" = employee."AgentInstallationId"
                  AND package."Id" = ai."PackageVersionId"
                  AND requirement.value->>'scope' IN ('team', 'board')
                  AND grant_row."Id" = md5(
                      ai."Id"::text || ':' || tm."TeamId"::text || ':' || requirement.value->>'name')::uuid;
                """);
        }
    }
}
