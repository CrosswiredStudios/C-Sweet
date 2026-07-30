using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftwareDeveloperWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedAgentInstallationId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedEmployeeId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AssignmentRevision",
                table: "CoreWorkTasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "DevelopmentBriefJson",
                table: "CoreWorkTasks",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GitRepositoryConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CloneUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    PermittedRepositoryPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    AuthenticationMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AllowedOperations = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefaultBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PullRequestProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AllowedHostsJson = table.Column<string>(type: "text", nullable: false),
                    AllowedPortsJson = table.Column<string>(type: "text", nullable: false),
                    SshHostFingerprintsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitRepositoryConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitRepositoryConnectionGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanReadFetch = table.Column<bool>(type: "boolean", nullable: false),
                    CanPushTicketBranch = table.Column<bool>(type: "boolean", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitRepositoryConnectionGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitRepositoryConnectionGrants_AgentInstallations_AgentInsta~",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GitRepositoryConnectionGrants_GitRepositoryConnections_Repo~",
                        column: x => x.RepositoryConnectionId,
                        principalTable: "GitRepositoryConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitTicketWorkspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentRevision = table.Column<long>(type: "bigint", nullable: false),
                    RepositoryConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspacePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    BaseBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BranchName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PullRequestUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ChangedFilesJson = table.Column<string>(type: "text", nullable: false),
                    ValidationsJson = table.Column<string>(type: "text", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitTicketWorkspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitTicketWorkspaces_AgentInstallations_AgentInstallationId",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GitTicketWorkspaces_GitRepositoryConnections_RepositoryConn~",
                        column: x => x.RepositoryConnectionId,
                        principalTable: "GitRepositoryConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_AssignedAgentInstallationId",
                table: "CoreWorkTasks",
                column: "AssignedAgentInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_AssignedEmployeeId",
                table: "CoreWorkTasks",
                column: "AssignedEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositoryConnectionGrants_AgentInstallationId",
                table: "GitRepositoryConnectionGrants",
                column: "AgentInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositoryConnectionGrants_RepositoryConnectionId_AgentI~",
                table: "GitRepositoryConnectionGrants",
                columns: new[] { "RepositoryConnectionId", "AgentInstallationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositoryConnections_OrganizationId_Name",
                table: "GitRepositoryConnections",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitTicketWorkspaces_AgentInstallationId_WorkItemId_Assignme~",
                table: "GitTicketWorkspaces",
                columns: new[] { "AgentInstallationId", "WorkItemId", "AssignmentRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitTicketWorkspaces_RepositoryConnectionId",
                table: "GitTicketWorkspaces",
                column: "RepositoryConnectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_AgentInstallations_AssignedAgentInstallationId",
                table: "CoreWorkTasks",
                column: "AssignedAgentInstallationId",
                principalTable: "AgentInstallations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_CoreOrganizationUsers_AssignedEmployeeId",
                table: "CoreWorkTasks",
                column: "AssignedEmployeeId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_AgentInstallations_AssignedAgentInstallationId",
                table: "CoreWorkTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_CoreOrganizationUsers_AssignedEmployeeId",
                table: "CoreWorkTasks");

            migrationBuilder.DropTable(
                name: "GitRepositoryConnectionGrants");

            migrationBuilder.DropTable(
                name: "GitTicketWorkspaces");

            migrationBuilder.DropTable(
                name: "GitRepositoryConnections");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_AssignedAgentInstallationId",
                table: "CoreWorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_AssignedEmployeeId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "AssignedAgentInstallationId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "AssignedEmployeeId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "AssignmentRevision",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "DevelopmentBriefJson",
                table: "CoreWorkTasks");
        }
    }
}
