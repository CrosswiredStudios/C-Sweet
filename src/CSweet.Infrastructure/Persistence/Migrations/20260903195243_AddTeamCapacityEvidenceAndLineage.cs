using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamCapacityEvidenceAndLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlternativesConsideredJson",
                table: "ResourceChangeRequests",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EvidenceJson",
                table: "ResourceChangeRequests",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExpectedEffect",
                table: "ResourceChangeRequests",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExpectedTeamRevision",
                table: "ResourceChangeRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstreamId",
                table: "ResourceChangeRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceChangeRequests_WorkstreamId",
                table: "ResourceChangeRequests",
                column: "WorkstreamId");

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceChangeRequests_Workstreams_WorkstreamId",
                table: "ResourceChangeRequests",
                column: "WorkstreamId",
                principalTable: "Workstreams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResourceChangeRequests_Workstreams_WorkstreamId",
                table: "ResourceChangeRequests");

            migrationBuilder.DropIndex(
                name: "IX_ResourceChangeRequests_WorkstreamId",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "AlternativesConsideredJson",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "EvidenceJson",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "ExpectedEffect",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "ExpectedTeamRevision",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "WorkstreamId",
                table: "ResourceChangeRequests");
        }
    }
}
