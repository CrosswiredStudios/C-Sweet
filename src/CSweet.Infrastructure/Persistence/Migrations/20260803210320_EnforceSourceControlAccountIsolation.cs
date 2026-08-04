using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSourceControlAccountIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SourceControlConnections_Provider_ProviderAccountId",
                table: "SourceControlConnections",
                columns: new[] { "Provider", "ProviderAccountId" },
                unique: true,
                filter: "\"Provider\" = 'GitHub'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceControlConnections_Provider_ProviderAccountId",
                table: "SourceControlConnections");
        }
    }
}
