using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManagedToolchainBuildRuntimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AgentRuntimeInstances_ActiveInstallation",
                table: "AgentRuntimeInstances");

            migrationBuilder.AddColumn<Guid>(
                name: "DeliveryBuildId",
                table: "AgentRuntimeInstances",
                type: "uuid",
                nullable: true);

            // Existing managed assignments already bind the runtime to an exact build.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "ExecutionWorkloadAssignments"
                        WHERE "WorkloadKind" = 'ToolchainBuild' AND "DeliveryBuildId" IS NOT NULL
                        GROUP BY "AgentRuntimeInstanceId" HAVING COUNT(DISTINCT "DeliveryBuildId") > 1) THEN
                        RAISE EXCEPTION 'A managed runtime is bound to multiple delivery builds.';
                    END IF;
                END $$;
                UPDATE "AgentRuntimeInstances" r SET "DeliveryBuildId" = a."DeliveryBuildId"
                FROM "ExecutionWorkloadAssignments" a
                WHERE a."AgentRuntimeInstanceId" = r."Id" AND a."WorkloadKind" = 'ToolchainBuild'
                    AND a."DeliveryBuildId" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_AgentRuntimeInstances_ActiveDeliveryBuild",
                table: "AgentRuntimeInstances",
                column: "DeliveryBuildId",
                unique: true,
                filter: "\"DeliveryBuildId\" IS NOT NULL AND \"Status\" IN ('Queued', 'Starting', 'WaitingForMcpSession', 'Running', 'CompletionReported', 'Stopping')");

            migrationBuilder.CreateIndex(
                name: "UX_AgentRuntimeInstances_ActiveInstallation",
                table: "AgentRuntimeInstances",
                column: "AgentInstallationId",
                unique: true,
                filter: "\"DeliveryBuildId\" IS NULL AND \"Status\" IN ('Queued', 'Starting', 'WaitingForMcpSession', 'Running', 'CompletionReported', 'Stopping')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "AgentRuntimeInstances" WHERE "DeliveryBuildId" IS NOT NULL
                        AND "Status" IN ('Queued', 'Starting', 'WaitingForMcpSession', 'Running', 'CompletionReported', 'Stopping')) THEN
                        RAISE EXCEPTION 'Stop managed toolchain builds before rolling back runtime isolation.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "UX_AgentRuntimeInstances_ActiveDeliveryBuild",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropIndex(
                name: "UX_AgentRuntimeInstances_ActiveInstallation",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "DeliveryBuildId",
                table: "AgentRuntimeInstances");

            migrationBuilder.CreateIndex(
                name: "UX_AgentRuntimeInstances_ActiveInstallation",
                table: "AgentRuntimeInstances",
                column: "AgentInstallationId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Starting', 'WaitingForMcpSession', 'Running', 'CompletionReported', 'Stopping')");
        }
    }
}
