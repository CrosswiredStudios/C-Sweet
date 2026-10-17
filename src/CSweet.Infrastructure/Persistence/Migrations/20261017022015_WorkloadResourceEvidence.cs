using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkloadResourceEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MemoryFailures",
                table: "WorkloadResourceProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "PeakWorkloadMemoryBytes",
                table: "WorkloadResourceProfiles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "SuccessfulSamples",
                table: "WorkloadResourceProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CheckpointCommandClass",
                table: "SourceControlWorkspaces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckpointInputFingerprint",
                table: "SourceControlWorkspaces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckpointSummaryJson",
                table: "SourceControlWorkspaces",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResourceResumeWorkItemId",
                table: "AgentWorkItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastOperationDigest",
                table: "AgentWorkAttempts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WorkSucceeded",
                table: "AgentWorkAttempts",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PeakWorkloadMemoryBytes",
                table: "AgentRuntimeInstances",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ResourceFailureCode",
                table: "AgentRuntimeInstances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResourceMeasurementsRecordedAt",
                table: "AgentRuntimeInstances",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MemoryFailures",
                table: "WorkloadResourceProfiles");

            migrationBuilder.DropColumn(
                name: "PeakWorkloadMemoryBytes",
                table: "WorkloadResourceProfiles");

            migrationBuilder.DropColumn(
                name: "SuccessfulSamples",
                table: "WorkloadResourceProfiles");

            migrationBuilder.DropColumn(
                name: "CheckpointCommandClass",
                table: "SourceControlWorkspaces");

            migrationBuilder.DropColumn(
                name: "CheckpointInputFingerprint",
                table: "SourceControlWorkspaces");

            migrationBuilder.DropColumn(
                name: "CheckpointSummaryJson",
                table: "SourceControlWorkspaces");

            migrationBuilder.DropColumn(
                name: "ResourceResumeWorkItemId",
                table: "AgentWorkItems");

            migrationBuilder.DropColumn(
                name: "LastOperationDigest",
                table: "AgentWorkAttempts");

            migrationBuilder.DropColumn(
                name: "WorkSucceeded",
                table: "AgentWorkAttempts");

            migrationBuilder.DropColumn(
                name: "PeakWorkloadMemoryBytes",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "ResourceFailureCode",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "ResourceMeasurementsRecordedAt",
                table: "AgentRuntimeInstances");
        }
    }
}
