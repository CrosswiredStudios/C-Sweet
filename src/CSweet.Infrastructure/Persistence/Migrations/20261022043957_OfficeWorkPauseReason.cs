using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfficeWorkPauseReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkPauseReason",
                table: "ExecutionNodes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Preserve ambiguous historical pauses. Only a durable, unresolved update
            // with explicit resume intent proves that maintenance owns the pause.
            migrationBuilder.Sql("""
                UPDATE "ExecutionNodes" AS office
                SET "WorkPauseReason" = CASE WHEN EXISTS (
                    SELECT 1 FROM "LocalOfficeSetupSessions" AS session
                    WHERE session."UpgradeOfficeId" = office."Id"
                        AND session."AutomaticUpdate" = TRUE
                        AND session."ResumeAfterUpdate" = TRUE
                        AND session."StopStartedAt" IS NOT NULL
                        AND session."WorkRestoredAt" IS NULL
                        AND session."UpdatePhase" IS DISTINCT FROM 'superseded'
                ) THEN 'Maintenance' ELSE 'User' END
                WHERE office."DrainingAt" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkPauseReason",
                table: "ExecutionNodes");
        }
    }
}
