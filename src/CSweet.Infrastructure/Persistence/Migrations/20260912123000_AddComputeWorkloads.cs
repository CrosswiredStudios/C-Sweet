using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20260912123000_AddComputeWorkloads")]
public sealed class AddComputeWorkloads : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("WorkloadJson", "ComputeOperations", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>("ResultJson", "ComputeOperations", type: "text", nullable: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("ResultJson", "ComputeOperations");
        migrationBuilder.DropColumn("WorkloadJson", "ComputeOperations");
    }
}
