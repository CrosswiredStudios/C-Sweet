using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpOnlyAgentRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventSubscriptionsJson",
                table: "AgentInstallationGrants",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<long>(
                name: "GrantRevision",
                table: "AgentInstallationGrants",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "ProvidedCapabilitiesJson",
                table: "AgentInstallationGrants",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "RequiredCapabilitiesJson",
                table: "AgentInstallationGrants",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ResourceLimitsJson",
                table: "AgentInstallationGrants",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.Sql("""
                UPDATE "AgentInstallationGrants"
                SET "ProvidedCapabilitiesJson" = "CapabilitiesJson",
                    "RequiredCapabilitiesJson" = "RequestedCapabilitiesJson",
                    "EventSubscriptionsJson" = "SubscriptionsJson",
                    "ResourceLimitsJson" = jsonb_build_object(
                        'maxRuntimeSeconds', "MaxRuntimeSeconds",
                        'memoryMb', "MemoryMb",
                        'cpuPercent', "CpuPercent")::text,
                    "GrantRevision" = 1;

                UPDATE "ExecutiveBriefingDeliveries"
                SET "Channel" = 'AgentRuntime'
                WHERE "Channel" = 'AgentBroker';
                """);

            migrationBuilder.CreateTable(
                name: "AgentCapabilityBindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequesterInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Capability = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantRevision = table.Column<long>(type: "bigint", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCapabilityBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCapabilityBindings_AgentInstallations_ProviderInstalla~",
                        column: x => x.ProviderInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCapabilityBindings_AgentInstallations_RequesterInstall~",
                        column: x => x.RequesterInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentWorkItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ProtectedPayload = table.Column<byte[]>(type: "bytea", nullable: false),
                    ProtectedResult = table.Column<byte[]>(type: "bytea", nullable: true),
                    ResultHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SourceType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    SourceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaximumAttempts = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentWorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentWorkItems_AgentInstallations_AgentInstallationId",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "McpAgentSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TickId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PackageVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    GrantRevision = table.Column<long>(type: "bigint", nullable: false),
                    AccessTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreviousAccessTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousTokenValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EstablishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRenewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpAgentSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpAgentSessions_AgentInstallations_AgentInstallationId",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_McpAgentSessions_AgentRuntimeInstances_RuntimeInstanceId",
                        column: x => x.RuntimeInstanceId,
                        principalTable: "AgentRuntimeInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentWorkAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    LeaseTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastProgressSequence = table.Column<long>(type: "bigint", nullable: false),
                    CompletionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentWorkAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentWorkAttempts_AgentRuntimeInstances_RuntimeInstanceId",
                        column: x => x.RuntimeInstanceId,
                        principalTable: "AgentRuntimeInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentWorkAttempts_AgentWorkItems_AgentWorkItemId",
                        column: x => x.AgentWorkItemId,
                        principalTable: "AgentWorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentWorkProgress",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentWorkAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    ProtectedValue = table.Column<byte[]>(type: "bytea", nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentWorkProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentWorkProgress_AgentWorkAttempts_AgentWorkAttemptId",
                        column: x => x.AgentWorkAttemptId,
                        principalTable: "AgentWorkAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentWorkProgress_AgentWorkItems_AgentWorkItemId",
                        column: x => x.AgentWorkItemId,
                        principalTable: "AgentWorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCapabilityBindings_ProviderInstallationId",
                table: "AgentCapabilityBindings",
                column: "ProviderInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCapabilityBindings_RequesterInstallationId_Capability",
                table: "AgentCapabilityBindings",
                columns: new[] { "RequesterInstallationId", "Capability" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentWorkAttempts_AgentWorkItemId_Attempt",
                table: "AgentWorkAttempts",
                columns: new[] { "AgentWorkItemId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentWorkAttempts_RuntimeInstanceId_LeaseExpiresAt",
                table: "AgentWorkAttempts",
                columns: new[] { "RuntimeInstanceId", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentWorkItems_AgentInstallationId_IdempotencyKey",
                table: "AgentWorkItems",
                columns: new[] { "AgentInstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentWorkItems_AgentInstallationId_Status_AvailableAt",
                table: "AgentWorkItems",
                columns: new[] { "AgentInstallationId", "Status", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentWorkProgress_AgentWorkAttemptId_Sequence",
                table: "AgentWorkProgress",
                columns: new[] { "AgentWorkAttemptId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentWorkProgress_AgentWorkItemId",
                table: "AgentWorkProgress",
                column: "AgentWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_McpAgentSessions_AccessTokenHash",
                table: "McpAgentSessions",
                column: "AccessTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpAgentSessions_AgentInstallationId",
                table: "McpAgentSessions",
                column: "AgentInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_McpAgentSessions_RuntimeInstanceId_RevokedAt",
                table: "McpAgentSessions",
                columns: new[] { "RuntimeInstanceId", "RevokedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCapabilityBindings");

            migrationBuilder.DropTable(
                name: "AgentWorkProgress");

            migrationBuilder.DropTable(
                name: "McpAgentSessions");

            migrationBuilder.DropTable(
                name: "AgentWorkAttempts");

            migrationBuilder.DropTable(
                name: "AgentWorkItems");

            migrationBuilder.DropColumn(
                name: "EventSubscriptionsJson",
                table: "AgentInstallationGrants");

            migrationBuilder.DropColumn(
                name: "GrantRevision",
                table: "AgentInstallationGrants");

            migrationBuilder.DropColumn(
                name: "ProvidedCapabilitiesJson",
                table: "AgentInstallationGrants");

            migrationBuilder.DropColumn(
                name: "RequiredCapabilitiesJson",
                table: "AgentInstallationGrants");

            migrationBuilder.DropColumn(
                name: "ResourceLimitsJson",
                table: "AgentInstallationGrants");
        }
    }
}
