using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CodingWorkloadResourceProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckpointAt",
                table: "SourceControlWorkspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckpointDigest",
                table: "SourceControlWorkspaces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CheckpointRuntimeId",
                table: "SourceControlWorkspaces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestedMemoryMb",
                table: "AgentWorkItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ResourceCheckpointDigest",
                table: "AgentWorkItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceProfileKey",
                table: "AgentWorkItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceRecoveryState",
                table: "AgentWorkItems",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResourceReplacementCount",
                table: "AgentWorkItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AllocatedMemoryMb",
                table: "AgentRuntimeInstances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CodingInitialMemoryMb",
                table: "AgentRuntimeGlobalSettings",
                type: "integer",
                nullable: false,
                defaultValue: 4096);

            migrationBuilder.AddColumn<int>(
                name: "CodingMaximumMemoryMb",
                table: "AgentRuntimeGlobalSettings",
                type: "integer",
                nullable: false,
                defaultValue: 16384);

            migrationBuilder.AddColumn<int>(
                name: "CodingMaximumReplacements",
                table: "AgentRuntimeGlobalSettings",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateTable(
                name: "WorkloadResourceProfiles",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandClass = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemoryMb = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkloadResourceProfiles", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkloadResourceProfiles");

            migrationBuilder.DropColumn(
                name: "CheckpointAt",
                table: "SourceControlWorkspaces");

            migrationBuilder.DropColumn(
                name: "CheckpointDigest",
                table: "SourceControlWorkspaces");

            migrationBuilder.DropColumn(
                name: "CheckpointRuntimeId",
                table: "SourceControlWorkspaces");

            migrationBuilder.DropColumn(
                name: "RequestedMemoryMb",
                table: "AgentWorkItems");

            migrationBuilder.DropColumn(
                name: "ResourceCheckpointDigest",
                table: "AgentWorkItems");

            migrationBuilder.DropColumn(
                name: "ResourceProfileKey",
                table: "AgentWorkItems");

            migrationBuilder.DropColumn(
                name: "ResourceRecoveryState",
                table: "AgentWorkItems");

            migrationBuilder.DropColumn(
                name: "ResourceReplacementCount",
                table: "AgentWorkItems");

            migrationBuilder.DropColumn(
                name: "AllocatedMemoryMb",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "CodingInitialMemoryMb",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropColumn(
                name: "CodingMaximumMemoryMb",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropColumn(
                name: "CodingMaximumReplacements",
                table: "AgentRuntimeGlobalSettings");
        }
    }
}
