using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BrokerMemoryReadEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "MemoryReadEvidenceVersion", table: "AgentRuntimeInstances", type: "integer", nullable: false, defaultValue: 0);
            migrationBuilder.CreateTable(
                name: "AgentMemoryReadReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuntimeId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    GrantRevision = table.Column<long>(type: "bigint", nullable: false),
                    Capability = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    AuthorityHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceiptHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentMemoryReadReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentMemoryReadReceipts_AgentRuntimeInstances_RuntimeId",
                        column: x => x.RuntimeId,
                        principalTable: "AgentRuntimeInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemoryReadReceipts_OrganizationId",
                table: "AgentMemoryReadReceipts",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemoryReadReceipts_RuntimeId_ReceiptHash",
                table: "AgentMemoryReadReceipts",
                columns: new[] { "RuntimeId", "ReceiptHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "AgentMemoryReadReceipts") THEN
                        RAISE EXCEPTION 'memory_read_evidence_downgrade_requires_snapshot';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(name: "MemoryReadEvidenceVersion", table: "AgentRuntimeInstances");
            migrationBuilder.DropTable(
                name: "AgentMemoryReadReceipts");
        }
    }
}
