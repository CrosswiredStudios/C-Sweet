using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEnterprisePluginAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PluginConnections_AgentInstallationId_DeclarationId",
                table: "PluginConnections");

            migrationBuilder.AddColumn<Guid>(
                name: "AccountGroupId",
                table: "PluginConnections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AuthorizationRevision",
                table: "PluginConnections",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "PluginAccountBusinessGrantId",
                table: "AgentCapabilityBindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PluginAccountConnectionId",
                table: "AgentCapabilityBindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PluginAccountResourceId",
                table: "AgentCapabilityBindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PluginAccountGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProviderProfile = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IdentityKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VerifiedSubject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IdentityAuthority = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginAccountGroups_AgentInstallations_PluginInstallationId",
                        column: x => x.PluginInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PluginAccountBusinessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SelectedResourceIdsJson = table.Column<string>(type: "text", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    PackageDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ApprovedByApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FutureResourcesAcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountBusinessGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginAccountBusinessGrants_AgentInstallations_PluginInstal~",
                        column: x => x.PluginInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PluginAccountBusinessGrants_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PluginAccountBusinessGrants_PluginAccountGroups_AccountGrou~",
                        column: x => x.AccountGroupId,
                        principalTable: "PluginAccountGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PluginAccountResources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalResourceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Handle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountResources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginAccountResources_AgentInstallations_PluginInstallatio~",
                        column: x => x.PluginInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PluginAccountResources_PluginAccountGroups_AccountGroupId",
                        column: x => x.AccountGroupId,
                        principalTable: "PluginAccountGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PluginAccountResourceAuthorizations",
                columns: table => new
                {
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountResourceAuthorizations", x => new { x.ResourceId, x.ConnectionId });
                    table.ForeignKey(
                        name: "FK_PluginAccountResourceAuthorizations_PluginAccountResources_~",
                        column: x => x.ResourceId,
                        principalTable: "PluginAccountResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PluginAccountResourceAuthorizations_PluginConnections_Conne~",
                        column: x => x.ConnectionId,
                        principalTable: "PluginConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginConnections_AccountGroupId",
                table: "PluginConnections",
                column: "AccountGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginConnections_AgentInstallationId_DeclarationId",
                table: "PluginConnections",
                columns: new[] { "AgentInstallationId", "DeclarationId" },
                unique: true,
                filter: "\"AccountGroupId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCapabilityBindings_PluginAccountBusinessGrantId",
                table: "AgentCapabilityBindings",
                column: "PluginAccountBusinessGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCapabilityBindings_PluginAccountConnectionId",
                table: "AgentCapabilityBindings",
                column: "PluginAccountConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCapabilityBindings_PluginAccountResourceId",
                table: "AgentCapabilityBindings",
                column: "PluginAccountResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountBusinessGrants_AccountGroupId",
                table: "PluginAccountBusinessGrants",
                column: "AccountGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountBusinessGrants_OrganizationId",
                table: "PluginAccountBusinessGrants",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountBusinessGrants_PluginInstallationId_AccountGro~",
                table: "PluginAccountBusinessGrants",
                columns: new[] { "PluginInstallationId", "AccountGroupId", "OrganizationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountGroups_PluginInstallationId_ProviderProfile",
                table: "PluginAccountGroups",
                columns: new[] { "PluginInstallationId", "ProviderProfile" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountResourceAuthorizations_ConnectionId",
                table: "PluginAccountResourceAuthorizations",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountResources_AccountGroupId_ResourceType_External~",
                table: "PluginAccountResources",
                columns: new[] { "AccountGroupId", "ResourceType", "ExternalResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountResources_PluginInstallationId",
                table: "PluginAccountResources",
                column: "PluginInstallationId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentCapabilityBindings_PluginAccountBusinessGrants_PluginA~",
                table: "AgentCapabilityBindings",
                column: "PluginAccountBusinessGrantId",
                principalTable: "PluginAccountBusinessGrants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentCapabilityBindings_PluginAccountResources_PluginAccoun~",
                table: "AgentCapabilityBindings",
                column: "PluginAccountResourceId",
                principalTable: "PluginAccountResources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentCapabilityBindings_PluginConnections_PluginAccountConn~",
                table: "AgentCapabilityBindings",
                column: "PluginAccountConnectionId",
                principalTable: "PluginConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PluginConnections_PluginAccountGroups_AccountGroupId",
                table: "PluginConnections",
                column: "AccountGroupId",
                principalTable: "PluginAccountGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentCapabilityBindings_PluginAccountBusinessGrants_PluginA~",
                table: "AgentCapabilityBindings");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentCapabilityBindings_PluginAccountResources_PluginAccoun~",
                table: "AgentCapabilityBindings");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentCapabilityBindings_PluginConnections_PluginAccountConn~",
                table: "AgentCapabilityBindings");

            migrationBuilder.DropForeignKey(
                name: "FK_PluginConnections_PluginAccountGroups_AccountGroupId",
                table: "PluginConnections");

            migrationBuilder.DropTable(
                name: "PluginAccountBusinessGrants");

            migrationBuilder.DropTable(
                name: "PluginAccountResourceAuthorizations");

            migrationBuilder.DropTable(
                name: "PluginAccountResources");

            migrationBuilder.DropTable(
                name: "PluginAccountGroups");

            migrationBuilder.DropIndex(
                name: "IX_PluginConnections_AccountGroupId",
                table: "PluginConnections");

            migrationBuilder.DropIndex(
                name: "IX_PluginConnections_AgentInstallationId_DeclarationId",
                table: "PluginConnections");

            migrationBuilder.DropIndex(
                name: "IX_AgentCapabilityBindings_PluginAccountBusinessGrantId",
                table: "AgentCapabilityBindings");

            migrationBuilder.DropIndex(
                name: "IX_AgentCapabilityBindings_PluginAccountConnectionId",
                table: "AgentCapabilityBindings");

            migrationBuilder.DropIndex(
                name: "IX_AgentCapabilityBindings_PluginAccountResourceId",
                table: "AgentCapabilityBindings");

            migrationBuilder.DropColumn(
                name: "AccountGroupId",
                table: "PluginConnections");

            migrationBuilder.DropColumn(
                name: "AuthorizationRevision",
                table: "PluginConnections");

            migrationBuilder.DropColumn(
                name: "PluginAccountBusinessGrantId",
                table: "AgentCapabilityBindings");

            migrationBuilder.DropColumn(
                name: "PluginAccountConnectionId",
                table: "AgentCapabilityBindings");

            migrationBuilder.DropColumn(
                name: "PluginAccountResourceId",
                table: "AgentCapabilityBindings");

            migrationBuilder.CreateIndex(
                name: "IX_PluginConnections_AgentInstallationId_DeclarationId",
                table: "PluginConnections",
                columns: new[] { "AgentInstallationId", "DeclarationId" },
                unique: true);
        }
    }
}
