using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrantApprovedProductManagersBoardCreation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                WITH eligible AS (
                    SELECT DISTINCT ON (
                        request."OrganizationId",
                        request."RequesterInstallationId")
                        request."Id" AS "RequestId",
                        request."OrganizationId",
                        request."RequesterInstallationId",
                        request."DecidedByOrganizationUserId",
                        COALESCE(request."DecidedAt", request."UpdatedAt") AS "GrantedAt"
                    FROM "ResourceChangeRequests" AS request
                    INNER JOIN "AgentInstallations" AS installation
                        ON installation."Id" = request."RequesterInstallationId"
                       AND installation."BusinessId" = request."OrganizationId"::text
                       AND installation."Scope" = 'Organization'
                       AND installation."IsEnabled" = TRUE
                       AND installation."RevisionStatus" = 'Active'
                    INNER JOIN "AgentInstallationGrants" AS package_grant
                        ON package_grant."AgentInstallationId" = installation."Id"
                    WHERE request."Status" = 'Approved'
                      AND request."DecidedByOrganizationUserId" IS NOT NULL
                      AND package_grant."RequiredCapabilitiesJson"::jsonb
                          @> '["work.board.create"]'::jsonb
                    ORDER BY
                        request."OrganizationId",
                        request."RequesterInstallationId",
                        request."DecidedAt" DESC NULLS LAST,
                        request."UpdatedAt" DESC
                )
                INSERT INTO "ScopedActionGrants" (
                    "Id",
                    "OrganizationId",
                    "SubjectKind",
                    "SubjectId",
                    "Action",
                    "ScopeKind",
                    "ScopeId",
                    "CanDelegate",
                    "ParentGrantId",
                    "GrantedBySubjectKind",
                    "GrantedBySubjectId",
                    "Revision",
                    "GrantedAt",
                    "ExpiresAt",
                    "RevokedAt")
                SELECT
                    md5('resource-change-board-create:' || eligible."RequestId"::text)::uuid,
                    eligible."OrganizationId",
                    'AgentInstallation',
                    eligible."RequesterInstallationId",
                    'work.board.create',
                    'Organization',
                    NULL,
                    FALSE,
                    NULL,
                    'OrganizationUser',
                    eligible."DecidedByOrganizationUserId",
                    1,
                    eligible."GrantedAt",
                    NULL,
                    NULL
                FROM eligible
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM "ScopedActionGrants" AS existing
                    WHERE existing."OrganizationId" = eligible."OrganizationId"
                      AND existing."SubjectKind" = 'AgentInstallation'
                      AND existing."SubjectId" = eligible."RequesterInstallationId"
                      AND existing."Action" = 'work.board.create'
                      AND existing."ScopeKind" = 'Organization'
                      AND existing."ScopeId" IS NULL
                      AND existing."RevokedAt" IS NULL
                      AND (existing."ExpiresAt" IS NULL OR existing."ExpiresAt" > NOW())
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "ScopedActionGrants" AS scoped_grant
                USING "ResourceChangeRequests" AS request
                WHERE scoped_grant."Id" =
                    md5('resource-change-board-create:' || request."Id"::text)::uuid
                  AND scoped_grant."OrganizationId" = request."OrganizationId"
                  AND scoped_grant."SubjectKind" = 'AgentInstallation'
                  AND scoped_grant."SubjectId" = request."RequesterInstallationId"
                  AND scoped_grant."Action" = 'work.board.create'
                  AND scoped_grant."ScopeKind" = 'Organization'
                  AND scoped_grant."ScopeId" IS NULL;
                """);
        }
    }
}
