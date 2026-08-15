using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RevokeLegacyExecutionNodesForOfficeCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "ExecutionWorkloadAssignments"
                SET "Status" = 'Fenced',
                    "FailureCode" = 'office-cutover',
                    "SanitizedFailure" = 'The legacy execution node was fenced during the Office v1 cutover.',
                    "CompletedAt" = CURRENT_TIMESTAMP,
                    "LeaseExpiresAt" = NULL,
                    "FencingEpoch" = "FencingEpoch" + 1
                WHERE "ExecutionNodeId" IS NOT NULL
                  AND "Status" IN ('Pending', 'Assigned', 'Starting', 'Running', 'Stopping');

                UPDATE "ExecutionNodes"
                SET "Status" = 'Revoked',
                    "RevokedAt" = COALESCE("RevokedAt", CURRENT_TIMESTAMP),
                    "UpdatedAt" = CURRENT_TIMESTAMP,
                    "SessionEpoch" = "SessionEpoch" + 1
                WHERE "Status" <> 'Revoked';

                UPDATE "ExecutionNodeEnrollments"
                SET "Status" = 'Revoked',
                    "RevokedAt" = COALESCE("RevokedAt", CURRENT_TIMESTAMP)
                WHERE "Status" <> 'Revoked';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revocation and fencing are intentionally irreversible security events.
        }
    }
}
