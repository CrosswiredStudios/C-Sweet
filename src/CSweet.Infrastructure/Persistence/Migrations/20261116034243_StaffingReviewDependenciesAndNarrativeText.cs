using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StaffingReviewDependenciesAndNarrativeText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(name: "Description", table: "OrganizationTeams", type: "text", nullable: false,
                oldClrType: typeof(string), oldType: "character varying(2048)", oldMaxLength: 2048);
            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "WorkforcePlans",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "Objective",
                table: "WorkforcePlans",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "ResourceChangeRoles",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<string>(
                name: "Timing",
                table: "ResourceChangeRoles",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "Team",
                table: "ResourceChangeRoles",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(160)",
                oldMaxLength: 160);

            migrationBuilder.AlterColumn<string>(
                name: "Purpose",
                table: "ResourceChangeRoles",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<string>(
                name: "TeamName",
                table: "ResourceChangeRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(160)",
                oldMaxLength: 160,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TeamDescription",
                table: "ResourceChangeRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Rationale",
                table: "ResourceChangeRequests",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4096)",
                oldMaxLength: 4096);

            migrationBuilder.AlterColumn<string>(
                name: "ProductGoal",
                table: "ResourceChangeRequests",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<string>(
                name: "ExpectedEffect",
                table: "ResourceChangeRequests",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RevisesRequestId",
                table: "ResourceChangeRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaitingDependencyJson",
                table: "CoreWorkTasks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceChangeRequests_OrganizationId_RequesterOrganization~",
                table: "ResourceChangeRequests",
                columns: new[] { "OrganizationId", "RequesterOrganizationUserId", "RevisesRequestId" },
                unique: true,
                filter: "\"RevisesRequestId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(name: "Description", table: "OrganizationTeams", type: "character varying(2048)", maxLength: 2048,
                nullable: false, oldClrType: typeof(string), oldType: "text");
            migrationBuilder.DropIndex(
                name: "IX_ResourceChangeRequests_OrganizationId_RequesterOrganization~",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "RevisesRequestId",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "WaitingDependencyJson",
                table: "CoreWorkTasks");

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "WorkforcePlans",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Objective",
                table: "WorkforcePlans",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "ResourceChangeRoles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Timing",
                table: "ResourceChangeRoles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Team",
                table: "ResourceChangeRoles",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Purpose",
                table: "ResourceChangeRoles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "TeamName",
                table: "ResourceChangeRequests",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TeamDescription",
                table: "ResourceChangeRequests",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Rationale",
                table: "ResourceChangeRequests",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "ProductGoal",
                table: "ResourceChangeRequests",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "ExpectedEffect",
                table: "ResourceChangeRequests",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
