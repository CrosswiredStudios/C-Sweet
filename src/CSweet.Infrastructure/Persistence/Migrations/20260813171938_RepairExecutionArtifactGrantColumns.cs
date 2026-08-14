using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairExecutionArtifactGrantColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DistributedExecutionFleet was amended after some environments had
            // already recorded it in __EFMigrationsHistory. Repair those schemas
            // without disturbing databases where the columns already exist.
            migrationBuilder.Sql(
                """
                ALTER TABLE "ExecutionWorkloadAssignments"
                    ADD COLUMN IF NOT EXISTS "ArtifactGrantTransferHash" character varying(64) NULL,
                    ADD COLUMN IF NOT EXISTS "ArtifactGrantInUseUntil" timestamp with time zone NULL,
                    ADD COLUMN IF NOT EXISTS "ArtifactGrantConsumedAt" timestamp with time zone NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally irreversible: these columns may have been created by
            // DistributedExecutionFleet in databases that never had schema drift.
        }
    }
}
