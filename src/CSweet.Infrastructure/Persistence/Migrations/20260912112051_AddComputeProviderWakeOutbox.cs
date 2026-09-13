using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComputeProviderWakeOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComputeProviderWakes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeProviderWakes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComputeProviderWakes_ComputeOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "ComputeOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeProviderWakes_OperationId",
                table: "ComputeProviderWakes",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComputeProviderWakes_OrganizationId_NodeId_ProviderId_Creat~",
                table: "ComputeProviderWakes",
                columns: new[] { "OrganizationId", "NodeId", "ProviderId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeProviderWakes_PublishedAt_NextAttemptAt",
                table: "ComputeProviderWakes",
                columns: new[] { "PublishedAt", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComputeProviderWakes");
        }
    }
}
