using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginAccountLifecycleEnforcement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextInventoryCheckAt",
                table: "PluginConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("UPDATE \"PluginConnections\" SET \"NextInventoryCheckAt\" = CURRENT_TIMESTAMP WHERE \"AccountGroupId\" IS NOT NULL AND \"Status\" = 'Connected';");

            migrationBuilder.AlterColumn<Guid>(
                name: "ApplicationUserId",
                table: "PluginAccountSetupSessions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "IsMaintenance",
                table: "PluginAccountSetupSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PluginAccountOperators",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "PluginAccountAdministrations",
                columns: table => new
                {
                    PluginInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RemovalRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountAdministrations", x => x.PluginInstallationId);
                });

            migrationBuilder.CreateTable(
                name: "PluginAccountDataCopies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConsumerInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Store = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountDataCopies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PluginAccountDeletions",
                columns: table => new
                {
                    AccountGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TargetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LocalCompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountDeletions", x => x.AccountGroupId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountOperators_PluginInstallationId_ApplicationUse~1",
                table: "PluginAccountOperators",
                columns: new[] { "PluginInstallationId", "ApplicationUserId", "AccountGroupId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountDataCopies_AccountGroupId_Store_RecordId",
                table: "PluginAccountDataCopies",
                columns: new[] { "AccountGroupId", "Store", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountDataCopies_ConsumerInstallationId_DeletedAt",
                table: "PluginAccountDataCopies",
                columns: new[] { "ConsumerInstallationId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountDeletions_PluginInstallationId_Status",
                table: "PluginAccountDeletions",
                columns: new[] { "PluginInstallationId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A system maintenance attempt has no human owner. Never fabricate one during rollback.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "PluginAccountSetupSessions" WHERE "ApplicationUserId" IS NULL) THEN
                        RAISE EXCEPTION 'Lifecycle maintenance records exist. Archive them explicitly before rolling back this migration.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "PluginAccountAdministrations");

            migrationBuilder.DropTable(
                name: "PluginAccountDataCopies");

            migrationBuilder.DropTable(
                name: "PluginAccountDeletions");

            migrationBuilder.DropIndex(
                name: "IX_PluginAccountOperators_PluginInstallationId_ApplicationUse~1",
                table: "PluginAccountOperators");

            migrationBuilder.DropColumn(
                name: "NextInventoryCheckAt",
                table: "PluginConnections");

            migrationBuilder.DropColumn(
                name: "IsMaintenance",
                table: "PluginAccountSetupSessions");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PluginAccountOperators");

            migrationBuilder.AlterColumn<Guid>(
                name: "ApplicationUserId",
                table: "PluginAccountSetupSessions",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
