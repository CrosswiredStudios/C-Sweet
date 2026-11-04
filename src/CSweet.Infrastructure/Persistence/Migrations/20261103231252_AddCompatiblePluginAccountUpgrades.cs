using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompatiblePluginAccountUpgrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PluginAccountUpgrades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalSetupSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ServiceOrigin = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    RemoteConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceRequestJson = table.Column<string>(type: "text", nullable: false),
                    ServiceReviewHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CompatibilityHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountUpgrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginAccountUpgrades_AgentInstallations_SourceInstallation~",
                        column: x => x.SourceInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PluginAccountUpgrades_AgentInstallations_TargetInstallation~",
                        column: x => x.TargetInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountUpgrades_SourceInstallationId",
                table: "PluginAccountUpgrades",
                column: "SourceInstallationId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.Sql("""
                CREATE FUNCTION csweet_plugin_upgrade_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP IN ('DELETE','TRUNCATE') THEN
                        RAISE EXCEPTION 'plugin_upgrade_evidence_immutable';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Status" <> 'Pending' OR NEW."Revision" <> 1 OR NEW."CompletedAt" IS NOT NULL
                            OR NEW."SourceInstallationId" = NEW."TargetInstallationId" THEN
                            RAISE EXCEPTION 'plugin_upgrade_pending_intent_required';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD."Status" <> 'Pending'
                        OR NEW."Status" NOT IN ('Completed','Cancelled','CancelledReviewRequired')
                        OR NEW."Revision" <> OLD."Revision" + 1 OR NEW."CompletedAt" IS NULL
                        OR (to_jsonb(NEW) - ARRAY['Status','Revision','CompletedAt']) IS DISTINCT FROM
                           (to_jsonb(OLD) - ARRAY['Status','Revision','CompletedAt']) THEN
                        RAISE EXCEPTION 'plugin_upgrade_authority_immutable';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER csweet_plugin_upgrade_guard BEFORE INSERT OR UPDATE OR DELETE ON "PluginAccountUpgrades"
                    FOR EACH ROW EXECUTE FUNCTION csweet_plugin_upgrade_guard();
                CREATE TRIGGER csweet_plugin_upgrade_truncate BEFORE TRUNCATE ON "PluginAccountUpgrades"
                    FOR EACH STATEMENT EXECUTE FUNCTION csweet_plugin_upgrade_guard();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountUpgrades_TargetInstallationId",
                table: "PluginAccountUpgrades",
                column: "TargetInstallationId",
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "PluginAccountUpgrades") THEN
                        RAISE EXCEPTION 'Retain compatible upgrade intent and receipt evidence before rollback.';
                    END IF;
                END $$;
                DROP TRIGGER csweet_plugin_upgrade_guard ON "PluginAccountUpgrades";
                DROP TRIGGER csweet_plugin_upgrade_truncate ON "PluginAccountUpgrades";
                DROP FUNCTION csweet_plugin_upgrade_guard();
                """);
            migrationBuilder.DropTable(
                name: "PluginAccountUpgrades");
        }
    }
}
