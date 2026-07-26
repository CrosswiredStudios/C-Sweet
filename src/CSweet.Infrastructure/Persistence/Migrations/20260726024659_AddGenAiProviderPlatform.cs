using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenAiProviderPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GenAiProviderProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ProviderType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ApiKeySecretName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSuccessfulConnectionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenAiProviderProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GenAiOperationConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TemplateJson = table.Column<string>(type: "text", nullable: true),
                    OutputSelector = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DefaultsJson = table.Column<string>(type: "text", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenAiOperationConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenAiOperationConfigurations_GenAiProviderProfiles_Provider~",
                        column: x => x.ProviderProfileId,
                        principalTable: "GenAiProviderProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GenAiJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    PromptHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestJson = table.Column<string>(type: "text", nullable: false),
                    ProviderJobId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenAiJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenAiJobs_GenAiOperationConfigurations_OperationConfigurati~",
                        column: x => x.OperationConfigurationId,
                        principalTable: "GenAiOperationConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GenAiOperationDefaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OperationConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GenAiOperationDefaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GenAiOperationDefaults_GenAiOperationConfigurations_Operati~",
                        column: x => x.OperationConfigurationId,
                        principalTable: "GenAiOperationConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatingAgentInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenAiJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaAssets_GenAiJobs_GenAiJobId",
                        column: x => x.GenAiJobId,
                        principalTable: "GenAiJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GenAiJobs_AgentInstallationId_OperationType_IdempotencyKey",
                table: "GenAiJobs",
                columns: new[] { "AgentInstallationId", "OperationType", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GenAiJobs_OperationConfigurationId",
                table: "GenAiJobs",
                column: "OperationConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_GenAiJobs_Status_CreatedAt",
                table: "GenAiJobs",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GenAiOperationConfigurations_ProviderProfileId_OperationTyp~",
                table: "GenAiOperationConfigurations",
                columns: new[] { "ProviderProfileId", "OperationType", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenAiOperationDefaults_OperationConfigurationId",
                table: "GenAiOperationDefaults",
                column: "OperationConfigurationId");

            migrationBuilder.CreateIndex(
                name: "IX_GenAiOperationDefaults_OperationType",
                table: "GenAiOperationDefaults",
                column: "OperationType",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_GenAiJobId",
                table: "MediaAssets",
                column: "GenAiJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_OrganizationId_CreatedAt",
                table: "MediaAssets",
                columns: new[] { "OrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_StorageKey",
                table: "MediaAssets",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GenAiOperationDefaults");

            migrationBuilder.DropTable(
                name: "MediaAssets");

            migrationBuilder.DropTable(
                name: "GenAiJobs");

            migrationBuilder.DropTable(
                name: "GenAiOperationConfigurations");

            migrationBuilder.DropTable(
                name: "GenAiProviderProfiles");
        }
    }
}
