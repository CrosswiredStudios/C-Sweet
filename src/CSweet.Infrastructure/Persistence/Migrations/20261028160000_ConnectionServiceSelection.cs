using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace CSweet.Infrastructure.Persistence.Migrations;
[DbContext(typeof(CSweetDbContext))]
[Migration("20261028160000_ConnectionServiceSelection")]
public sealed class ConnectionServiceSelection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(name: "ConnectionServiceSelectionJson", table: "SystemConfigurations", type: "text", nullable: true);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(name: "ConnectionServiceSelectionJson", table: "SystemConfigurations");
}
