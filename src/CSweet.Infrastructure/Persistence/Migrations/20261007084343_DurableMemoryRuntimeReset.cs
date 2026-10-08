using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableMemoryRuntimeReset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MemoryResetCompletedAt",
                table: "AgentRuntimeInstances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemoryResetReasonCode",
                table: "AgentRuntimeInstances",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MemoryResetRequestedAt",
                table: "AgentRuntimeInstances",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "AgentRuntimeInstances" WHERE "MemoryResetRequestedAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'memory_runtime_reset_downgrade_requires_snapshot';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "MemoryResetCompletedAt",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "MemoryResetReasonCode",
                table: "AgentRuntimeInstances");

            migrationBuilder.DropColumn(
                name: "MemoryResetRequestedAt",
                table: "AgentRuntimeInstances");
        }
    }
}
