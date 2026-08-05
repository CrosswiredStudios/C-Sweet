using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVaultBackedPluginProviderProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PluginProviderProfiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AuthorizationEndpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    TokenEndpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    RevocationEndpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ProtectedClientSecret = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginProviderProfiles", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginProviderProfiles");
        }
    }
}
