using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceAgentContainersWithCertifiedVmIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing shared-kernel workloads and Docker-produced artifacts cannot be
            // trusted across this boundary change. Disable installations, remove all
            // runtime/build state, and queue clean VM builds for approved packages.
            migrationBuilder.Sql("""
                UPDATE "AgentInstallations"
                SET "IsEnabled" = FALSE
                WHERE "PackageVersionId" IS NOT NULL;

                DELETE FROM "AgentRuntimeInstances";
                DELETE FROM "AgentBuildJobs";

                UPDATE "AgentPackageVersions"
                SET "Status" = 'Approved',
                    "PackageDigest" = NULL,
                    "PackagePath" = NULL,
                    "BuiltAt" = NULL
                WHERE "Status" IN ('Approved', 'Built', 'Failed');

                INSERT INTO "AgentBuildJobs"
                    ("Id", "PackageVersionId", "Attempt", "Status", "StepsJson", "QueuedAt")
                SELECT gen_random_uuid(), "Id", 1, 'Queued', '[]', NOW()
                FROM "AgentPackageVersions"
                WHERE "Status" = 'Approved';
                """);

            migrationBuilder.DropColumn(
                name: "ContainerId",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "ContainerName",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "DotNetBuilderImage",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropColumn(
                name: "DotNetRuntimeBaseImage",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.RenameColumn(
                name: "WorkloadTokenHash",
                table: "AgentRuntimeInstances",
                newName: "BrokerTokenHash");

            migrationBuilder.RenameColumn(
                name: "RemoveContainersAfterCompletion",
                table: "AgentRuntimeGlobalSettings",
                newName: "RemoveWorkloadsAfterCompletion");

            migrationBuilder.RenameColumn(
                name: "PerInstallationMaxActiveContainers",
                table: "AgentRuntimeGlobalSettings",
                newName: "PerInstallationMaxActiveWorkloads");

            migrationBuilder.RenameColumn(
                name: "PerBusinessMaxActiveContainers",
                table: "AgentRuntimeGlobalSettings",
                newName: "PerBusinessMaxActiveWorkloads");

            migrationBuilder.RenameColumn(
                name: "MaximumContainerMemoryMb",
                table: "AgentRuntimeGlobalSettings",
                newName: "MaximumWorkloadMemoryMb");

            migrationBuilder.RenameColumn(
                name: "MaximumContainerCpuPercent",
                table: "AgentRuntimeGlobalSettings",
                newName: "MaximumWorkloadCpuPercent");

            migrationBuilder.RenameColumn(
                name: "GlobalMaxActiveContainers",
                table: "AgentRuntimeGlobalSettings",
                newName: "GlobalMaxActiveWorkloads");

            migrationBuilder.RenameColumn(
                name: "DefaultContainerPidsLimit",
                table: "AgentRuntimeGlobalSettings",
                newName: "DefaultWorkloadProcessLimit");

            migrationBuilder.RenameColumn(
                name: "DefaultContainerMemoryMb",
                table: "AgentRuntimeGlobalSettings",
                newName: "DefaultWorkloadMemoryMb");

            migrationBuilder.RenameColumn(
                name: "DefaultContainerLogLimitMb",
                table: "AgentRuntimeGlobalSettings",
                newName: "DefaultWorkloadLogLimitMb");

            migrationBuilder.RenameColumn(
                name: "DefaultContainerCpuPercent",
                table: "AgentRuntimeGlobalSettings",
                newName: "DefaultWorkloadCpuPercent");

            migrationBuilder.RenameColumn(
                name: "ContainerStopGraceSeconds",
                table: "AgentRuntimeGlobalSettings",
                newName: "WorkloadStopGraceSeconds");

            migrationBuilder.RenameColumn(
                name: "ContainerStartTimeoutSeconds",
                table: "AgentRuntimeGlobalSettings",
                newName: "WorkloadStartTimeoutSeconds");

            migrationBuilder.AddColumn<string>(
                name: "IsolationProviderId",
                table: "AgentRuntimeInstances",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderInstanceId",
                table: "AgentRuntimeInstances",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtifactArchitecture",
                table: "AgentPackageVersions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "x64");

            migrationBuilder.AddColumn<string>(
                name: "ArtifactFormatVersion",
                table: "AgentPackageVersions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "1.0");

            migrationBuilder.AddColumn<string>(
                name: "ArtifactOperatingSystem",
                table: "AgentPackageVersions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "linux");

            migrationBuilder.AddColumn<string>(
                name: "ArtifactSignature",
                table: "AgentPackageVersions",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsolationProviderId",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "ProviderInstanceId",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "ArtifactArchitecture",
                table: "AgentPackageVersions");

            migrationBuilder.DropColumn(
                name: "ArtifactFormatVersion",
                table: "AgentPackageVersions");

            migrationBuilder.DropColumn(
                name: "ArtifactOperatingSystem",
                table: "AgentPackageVersions");

            migrationBuilder.DropColumn(
                name: "ArtifactSignature",
                table: "AgentPackageVersions");

            migrationBuilder.RenameColumn(
                name: "BrokerTokenHash",
                table: "AgentRuntimeInstances",
                newName: "WorkloadTokenHash");

            migrationBuilder.RenameColumn(
                name: "PerInstallationMaxActiveWorkloads",
                table: "AgentRuntimeGlobalSettings",
                newName: "PerInstallationMaxActiveContainers");

            migrationBuilder.RenameColumn(
                name: "PerBusinessMaxActiveWorkloads",
                table: "AgentRuntimeGlobalSettings",
                newName: "PerBusinessMaxActiveContainers");

            migrationBuilder.RenameColumn(
                name: "RemoveWorkloadsAfterCompletion",
                table: "AgentRuntimeGlobalSettings",
                newName: "RemoveContainersAfterCompletion");

            migrationBuilder.RenameColumn(
                name: "MaximumWorkloadMemoryMb",
                table: "AgentRuntimeGlobalSettings",
                newName: "MaximumContainerMemoryMb");

            migrationBuilder.RenameColumn(
                name: "MaximumWorkloadCpuPercent",
                table: "AgentRuntimeGlobalSettings",
                newName: "MaximumContainerCpuPercent");

            migrationBuilder.RenameColumn(
                name: "GlobalMaxActiveWorkloads",
                table: "AgentRuntimeGlobalSettings",
                newName: "GlobalMaxActiveContainers");

            migrationBuilder.RenameColumn(
                name: "DefaultWorkloadProcessLimit",
                table: "AgentRuntimeGlobalSettings",
                newName: "DefaultContainerPidsLimit");

            migrationBuilder.RenameColumn(
                name: "DefaultWorkloadMemoryMb",
                table: "AgentRuntimeGlobalSettings",
                newName: "DefaultContainerMemoryMb");

            migrationBuilder.RenameColumn(
                name: "DefaultWorkloadLogLimitMb",
                table: "AgentRuntimeGlobalSettings",
                newName: "DefaultContainerLogLimitMb");

            migrationBuilder.RenameColumn(
                name: "DefaultWorkloadCpuPercent",
                table: "AgentRuntimeGlobalSettings",
                newName: "DefaultContainerCpuPercent");

            migrationBuilder.RenameColumn(
                name: "WorkloadStopGraceSeconds",
                table: "AgentRuntimeGlobalSettings",
                newName: "ContainerStopGraceSeconds");

            migrationBuilder.RenameColumn(
                name: "WorkloadStartTimeoutSeconds",
                table: "AgentRuntimeGlobalSettings",
                newName: "ContainerStartTimeoutSeconds");

            migrationBuilder.AddColumn<string>(
                name: "ContainerId",
                table: "AgentRuntimeInstances",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContainerName",
                table: "AgentRuntimeInstances",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DotNetBuilderImage",
                table: "AgentRuntimeGlobalSettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DotNetRuntimeBaseImage",
                table: "AgentRuntimeGlobalSettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }
    }
}
