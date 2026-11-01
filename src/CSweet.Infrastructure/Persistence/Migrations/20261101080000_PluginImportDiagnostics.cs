using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20261101080000_PluginImportDiagnostics")]
public sealed class PluginImportDiagnostics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "ImportDiagnosticsJson", table: "PluginAccountSetupSessions", type: "text", nullable: true);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "ImportDiagnosticsJson", table: "PluginAccountSetupSessions");
}
