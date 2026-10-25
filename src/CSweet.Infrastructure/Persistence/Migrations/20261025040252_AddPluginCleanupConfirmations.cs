using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginCleanupConfirmations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CleanupNextAttemptAt",
                table: "PluginAccountDataCopies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PluginAccountCleanupReceipts",
                columns: table => new
                {
                    CopyId = table.Column<Guid>(type: "uuid", nullable: false),
                    CopyRevision = table.Column<long>(type: "bigint", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Payload = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    Signature = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountCleanupReceipts", x => x.CopyId);
                });
            migrationBuilder.Sql("""
                CREATE FUNCTION csweet_plugin_cleanup_receipt_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'plugin_cleanup_receipt_immutable'; END $$;
                CREATE TRIGGER csweet_plugin_cleanup_receipt_immutable BEFORE UPDATE OR DELETE ON "PluginAccountCleanupReceipts"
                    FOR EACH ROW EXECUTE FUNCTION csweet_plugin_cleanup_receipt_immutable();
                CREATE TRIGGER csweet_plugin_cleanup_receipt_truncate BEFORE TRUNCATE ON "PluginAccountCleanupReceipts"
                    FOR EACH STATEMENT EXECUTE FUNCTION csweet_plugin_cleanup_receipt_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "PluginAccountCleanupReceipts") THEN
                        RAISE EXCEPTION 'Cleanup receipts exist. Retain the signed evidence before rolling back.';
                    END IF;
                END $$;
                DROP TRIGGER csweet_plugin_cleanup_receipt_immutable ON "PluginAccountCleanupReceipts";
                DROP TRIGGER csweet_plugin_cleanup_receipt_truncate ON "PluginAccountCleanupReceipts";
                DROP FUNCTION csweet_plugin_cleanup_receipt_immutable();
                """);
            migrationBuilder.DropTable(
                name: "PluginAccountCleanupReceipts");

            migrationBuilder.DropColumn(
                name: "CleanupNextAttemptAt",
                table: "PluginAccountDataCopies");
        }
    }
}
