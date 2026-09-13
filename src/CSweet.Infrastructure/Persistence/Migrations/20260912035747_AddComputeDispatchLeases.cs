using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComputeDispatchLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DispatchLeaseExpiresAt",
                table: "ComputeOperations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DispatchLeaseId",
                table: "ComputeOperations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DispatchLeaseExpiresAt",
                table: "ComputeOperations");

            migrationBuilder.DropColumn(
                name: "DispatchLeaseId",
                table: "ComputeOperations");
        }
    }
}
