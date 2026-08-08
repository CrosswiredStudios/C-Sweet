using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnchorMcpTimeoutToGuestReady : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "McpSessionWaitingAt",
                table: "AgentRuntimeInstances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "AgentRuntimeInstances" AS runtime
                SET "McpSessionWaitingAt" = COALESCE(
                    (
                        SELECT MAX(event."OccurredAt")
                        FROM "AgentRuntimeEvents" AS event
                        WHERE event."AgentRuntimeInstanceId" = runtime."Id"
                          AND event."Status" = 'WaitingForMcpSession'
                    ),
                    runtime."StartedAt",
                    NOW())
                WHERE runtime."Status" = 'WaitingForMcpSession';

                UPDATE "AgentRuntimeGlobalSettings"
                SET "DefaultWorkloadMemoryMb" = GREATEST("DefaultWorkloadMemoryMb", 1024),
                    "MaximumWorkloadMemoryMb" = GREATEST("MaximumWorkloadMemoryMb", 1024);

                UPDATE "AgentInstallationGrants" AS installation_grant
                SET "MemoryMb" = 1024,
                    "ResourceLimitsJson" = jsonb_build_object(
                        'MaxRuntimeSeconds', installation_grant."MaxRuntimeSeconds",
                        'MemoryMb', 1024,
                        'CpuPercent', installation_grant."CpuPercent")::text
                FROM "AgentInstallations" AS installation
                INNER JOIN "AgentPackageVersions" AS package
                    ON package."Id" = installation."PackageVersionId"
                WHERE installation_grant."AgentInstallationId" = installation."Id"
                  AND package."PublisherId" = 'com.csweet'
                  AND installation_grant."MemoryMb" < 1024;

                UPDATE "AgentSchedules" AS schedule
                SET "ConsecutiveStartupFailures" = 0,
                    "AutomaticStartSuppressedAt" = NULL,
                    "NextTickAt" = NULL
                FROM "AgentInstallations" AS installation
                INNER JOIN "AgentPackageVersions" AS package
                    ON package."Id" = installation."PackageVersionId"
                WHERE schedule."AgentInstallationId" = installation."Id"
                  AND package."PublisherId" = 'com.csweet'
                  AND schedule."ActivationMode" = 'AlwaysOn'
                  AND schedule."AutomaticStartSuppressedAt" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "McpSessionWaitingAt",
                table: "AgentRuntimeInstances");
        }
    }
}
