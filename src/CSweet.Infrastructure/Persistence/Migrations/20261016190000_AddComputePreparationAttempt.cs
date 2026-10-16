using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20261016190000_AddComputePreparationAttempt")]
public sealed class AddComputePreparationAttempt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<Guid>(name: "PreparationAttemptId", table: "ComputeLocalSetups", type: "uuid", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "PreparationAttemptId", table: "ComputeLocalSetups");
}
