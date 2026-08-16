using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistedLocalOfficeSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocalOfficeSetupSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionNodeEnrollmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutionNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    HandoffSecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MachineBindingHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperatingSystem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Architecture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ControlPlaneOrigin = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ControlPlaneCertificateSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PresetKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AllocatableCpuCount = table.Column<int>(type: "integer", nullable: false),
                    AllocatableMemoryMb = table.Column<int>(type: "integer", nullable: false),
                    AllocatableDiskMb = table.Column<int>(type: "integer", nullable: false),
                    MaximumConcurrentWorkloads = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalOfficeSetupSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LocalOfficeSetupSessions_ExecutionNodeEnrollments_Execution~",
                        column: x => x.ExecutionNodeEnrollmentId,
                        principalTable: "ExecutionNodeEnrollments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LocalOfficeSetupSessions_ExecutionNodes_ExecutionNodeId",
                        column: x => x.ExecutionNodeId,
                        principalTable: "ExecutionNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalOfficeSetupSessions_CreatedByUserId_CreatedAt",
                table: "LocalOfficeSetupSessions",
                columns: new[] { "CreatedByUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalOfficeSetupSessions_ExecutionNodeEnrollmentId",
                table: "LocalOfficeSetupSessions",
                column: "ExecutionNodeEnrollmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalOfficeSetupSessions_ExecutionNodeId",
                table: "LocalOfficeSetupSessions",
                column: "ExecutionNodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalOfficeSetupSessions_HandoffSecretHash",
                table: "LocalOfficeSetupSessions",
                column: "HandoffSecretHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalOfficeSetupSessions");
        }
    }
}
