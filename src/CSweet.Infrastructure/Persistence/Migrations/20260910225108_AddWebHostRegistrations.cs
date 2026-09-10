using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebHostRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebHostRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IdentityPublicKeyBase64 = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IdentityKeyDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    MaximumCapacityJson = table.Column<string>(type: "text", nullable: false),
                    BootstrapJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReportedHeartbeatJson = table.Column<string>(type: "text", nullable: true),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebHostRegistrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebHostRegistrations_AgentInstallations_ProviderInstallatio~",
                        column: x => x.ProviderInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebHostRegistrations_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebHostRegistrations_IdentityKeyDigest",
                table: "WebHostRegistrations",
                column: "IdentityKeyDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebHostRegistrations_OrganizationId_RegistrationRequestId",
                table: "WebHostRegistrations",
                columns: new[] { "OrganizationId", "RegistrationRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebHostRegistrations_OrganizationId_Status_LastHeartbeatAt",
                table: "WebHostRegistrations",
                columns: new[] { "OrganizationId", "Status", "LastHeartbeatAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebHostRegistrations_ProviderInstallationId",
                table: "WebHostRegistrations",
                column: "ProviderInstallationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebHostRegistrations");
        }
    }
}
