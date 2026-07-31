using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailDeliveryProfilesAndUserDisplayName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailDeliveryConfigurations");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "AspNetUsers",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "Administrator");

            migrationBuilder.CreateTable(
                name: "EmailDeliveryProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    EnableSsl = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    EncryptedPassword = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    FromName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PublicAppUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    ConfiguredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastTestSucceededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDeliveryProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailDeliveryProfiles_IsDefault",
                table: "EmailDeliveryProfiles",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailDeliveryProfiles");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "AspNetUsers");

            migrationBuilder.CreateTable(
                name: "EmailDeliveryConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfiguredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EnableSsl = table.Column<bool>(type: "boolean", nullable: false),
                    EncryptedPassword = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    FromName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    LastTestSucceededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    PublicAppUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDeliveryConfigurations", x => x.Id);
                });
        }
    }
}
