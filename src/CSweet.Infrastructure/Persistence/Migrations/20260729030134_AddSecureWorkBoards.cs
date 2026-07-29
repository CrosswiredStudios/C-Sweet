using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecureWorkBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BoardId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScopedActionGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ScopeKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CanDelegate = table.Column<bool>(type: "boolean", nullable: false),
                    ParentGrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedBySubjectKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GrantedBySubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScopedActionGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScopedActionGrants_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScopedActionGrants_ScopedActionGrants_ParentGrantId",
                        column: x => x.ParentGrantId,
                        principalTable: "ScopedActionGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkBoards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkBoards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkBoards_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkBoards_Workstreams_WorkstreamId",
                        column: x => x.WorkstreamId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "WorkBoardColumns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    WipPolicy = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    WipLimit = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkBoardColumns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkBoardColumns_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkBoardUserPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false),
                    LastVisitedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkBoardUserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkBoardUserPreferences_CoreOrganizationUsers_Organization~",
                        column: x => x.OrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkBoardUserPreferences_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_BoardId",
                table: "CoreWorkTasks",
                column: "BoardId");

            migrationBuilder.CreateIndex(
                name: "IX_ScopedActionGrants_OrganizationId_Action_RevokedAt",
                table: "ScopedActionGrants",
                columns: new[] { "OrganizationId", "Action", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScopedActionGrants_OrganizationId_SubjectKind_SubjectId_Act~",
                table: "ScopedActionGrants",
                columns: new[] { "OrganizationId", "SubjectKind", "SubjectId", "Action", "ScopeKind", "ScopeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScopedActionGrants_ParentGrantId",
                table: "ScopedActionGrants",
                column: "ParentGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoardColumns_BoardId_Position",
                table: "WorkBoardColumns",
                columns: new[] { "BoardId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoards_OrganizationId_IsDefault",
                table: "WorkBoards",
                columns: new[] { "OrganizationId", "IsDefault" },
                unique: true,
                filter: "\"IsDefault\" = TRUE AND \"ArchivedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoards_OrganizationId_Name",
                table: "WorkBoards",
                columns: new[] { "OrganizationId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoards_WorkstreamId",
                table: "WorkBoards",
                column: "WorkstreamId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoardUserPreferences_BoardId_OrganizationUserId",
                table: "WorkBoardUserPreferences",
                columns: new[] { "BoardId", "OrganizationUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoardUserPreferences_OrganizationUserId",
                table: "WorkBoardUserPreferences",
                column: "OrganizationUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_WorkBoards_BoardId",
                table: "CoreWorkTasks",
                column: "BoardId",
                principalTable: "WorkBoards",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_WorkBoards_BoardId",
                table: "CoreWorkTasks");

            migrationBuilder.DropTable(
                name: "ScopedActionGrants");

            migrationBuilder.DropTable(
                name: "WorkBoardColumns");

            migrationBuilder.DropTable(
                name: "WorkBoardUserPreferences");

            migrationBuilder.DropTable(
                name: "WorkBoards");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_BoardId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "CoreWorkTasks");
        }
    }
}
