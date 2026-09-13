using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComputeProviderResultReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastResultDigest",
                table: "ComputeOperations",
                type: "character varying(71)",
                maxLength: 71,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastResultSequence",
                table: "ComputeOperations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastResultDigest",
                table: "ComputeOperations");

            migrationBuilder.DropColumn(
                name: "LastResultSequence",
                table: "ComputeOperations");
        }
    }
}
