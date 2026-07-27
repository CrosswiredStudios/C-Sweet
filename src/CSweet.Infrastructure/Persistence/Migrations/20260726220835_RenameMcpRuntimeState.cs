using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameMcpRuntimeState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AgentRuntimeInstances_ActiveInstallation",
                table: "AgentRuntimeInstances");

            migrationBuilder.RenameColumn(
                name: "BrokerRegisteredAt",
                table: "AgentRuntimeInstances",
                newName: "McpSessionEstablishedAt");

            migrationBuilder.RenameColumn(
                name: "BrokerRegistrationTimeoutSeconds",
                table: "AgentRuntimeGlobalSettings",
                newName: "McpSessionTimeoutSeconds");

            migrationBuilder.Sql("""
                UPDATE "AgentRuntimeInstances"
                SET "Status" = CASE
                    WHEN "Status" = 'WaitingForBrokerRegistration' THEN 'WaitingForMcpSession'
                    WHEN "Status" = 'BrokerRegistrationTimedOut' THEN 'McpSessionTimedOut'
                    ELSE "Status"
                END;

                UPDATE "AgentRuntimeGlobalSettings"
                SET "DefaultNetworkPolicy" = 'McpOnly'
                WHERE "DefaultNetworkPolicy" = 'BrokerOnly';
                """);

            migrationBuilder.CreateIndex(
                name: "UX_AgentRuntimeInstances_ActiveInstallation",
                table: "AgentRuntimeInstances",
                column: "AgentInstallationId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Starting', 'WaitingForMcpSession', 'Running', 'CompletionReported', 'Stopping')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_AgentRuntimeInstances_ActiveInstallation",
                table: "AgentRuntimeInstances");

            migrationBuilder.RenameColumn(
                name: "McpSessionEstablishedAt",
                table: "AgentRuntimeInstances",
                newName: "BrokerRegisteredAt");

            migrationBuilder.RenameColumn(
                name: "McpSessionTimeoutSeconds",
                table: "AgentRuntimeGlobalSettings",
                newName: "BrokerRegistrationTimeoutSeconds");

            migrationBuilder.Sql("""
                UPDATE "AgentRuntimeInstances"
                SET "Status" = CASE
                    WHEN "Status" = 'WaitingForMcpSession' THEN 'WaitingForBrokerRegistration'
                    WHEN "Status" = 'McpSessionTimedOut' THEN 'BrokerRegistrationTimedOut'
                    ELSE "Status"
                END;

                UPDATE "AgentRuntimeGlobalSettings"
                SET "DefaultNetworkPolicy" = 'BrokerOnly'
                WHERE "DefaultNetworkPolicy" = 'McpOnly';
                """);

            migrationBuilder.CreateIndex(
                name: "UX_AgentRuntimeInstances_ActiveInstallation",
                table: "AgentRuntimeInstances",
                column: "AgentInstallationId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Starting', 'WaitingForBrokerRegistration', 'Running', 'CompletionReported', 'Stopping')");
        }
    }
}
