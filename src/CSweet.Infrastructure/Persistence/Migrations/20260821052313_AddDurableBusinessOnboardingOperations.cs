using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableBusinessOnboardingOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessOnboardingOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatedByApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    BusinessName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Industry = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    MissionStatement = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    ChiefDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ChiefAgentPackageVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChiefAgentDefinitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChiefAgentInstallRequestJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResultOrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultActionUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessOnboardingOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessOnboardingOperations_AgentDefinitions_ChiefAgentDef~",
                        column: x => x.ChiefAgentDefinitionId,
                        principalTable: "AgentDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BusinessOnboardingOperations_CoreOrganizations_ResultOrgani~",
                        column: x => x.ResultOrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessOnboardingOperations_ChiefAgentDefinitionId",
                table: "BusinessOnboardingOperations",
                column: "ChiefAgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessOnboardingOperations_InitiatedByApplicationUserId_D~",
                table: "BusinessOnboardingOperations",
                columns: new[] { "InitiatedByApplicationUserId", "DismissedAt", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessOnboardingOperations_InitiatedByApplicationUserId_I~",
                table: "BusinessOnboardingOperations",
                columns: new[] { "InitiatedByApplicationUserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessOnboardingOperations_ResultOrganizationId",
                table: "BusinessOnboardingOperations",
                column: "ResultOrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessOnboardingOperations_Status_LeaseUntil",
                table: "BusinessOnboardingOperations",
                columns: new[] { "Status", "LeaseUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessOnboardingOperations");
        }
    }
}
