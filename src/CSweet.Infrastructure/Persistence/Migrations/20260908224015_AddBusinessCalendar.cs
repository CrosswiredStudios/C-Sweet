using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessCalendarChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventRevision = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendarChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessCalendarChanges_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BusinessCalendarEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchedulingOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchedulingInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    CreationKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendarEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessCalendarEvents_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BusinessCalendars",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeZoneId = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendars", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_BusinessCalendars_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BusinessCalendarDispatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurrenceLocal = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendarDispatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessCalendarDispatches_BusinessCalendarEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "BusinessCalendarEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BusinessCalendarExceptions",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurrenceLocal = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: true),
                    Cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    SchedulingOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchedulingInstallationId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendarExceptions", x => new { x.EventId, x.OccurrenceLocal });
                    table.ForeignKey(
                        name: "FK_BusinessCalendarExceptions_BusinessCalendarEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "BusinessCalendarEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BusinessCalendarReminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Read = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendarReminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessCalendarReminders_BusinessCalendarEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "BusinessCalendarEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessCalendarChanges_OrganizationId_Id",
                table: "BusinessCalendarChanges",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessCalendarDispatches_EventId_OccurrenceLocal_Kind_Rec~",
                table: "BusinessCalendarDispatches",
                columns: new[] { "EventId", "OccurrenceLocal", "Kind", "RecipientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessCalendarDispatches_Status_DueAt",
                table: "BusinessCalendarDispatches",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessCalendarEvents_OrganizationId_CreationKey",
                table: "BusinessCalendarEvents",
                columns: new[] { "OrganizationId", "CreationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessCalendarReminders_EventId",
                table: "BusinessCalendarReminders",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessCalendarReminders_OrganizationId_RecipientId_Read",
                table: "BusinessCalendarReminders",
                columns: new[] { "OrganizationId", "RecipientId", "Read" });
            migrationBuilder.Sql("""
                INSERT INTO "BusinessCalendars" ("OrganizationId", "TimeZoneId", "Revision")
                SELECT "Id", 'UTC', 1 FROM "CoreOrganizations"
                ON CONFLICT ("OrganizationId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessCalendarChanges");

            migrationBuilder.DropTable(
                name: "BusinessCalendarDispatches");

            migrationBuilder.DropTable(
                name: "BusinessCalendarExceptions");

            migrationBuilder.DropTable(
                name: "BusinessCalendarReminders");

            migrationBuilder.DropTable(
                name: "BusinessCalendars");

            migrationBuilder.DropTable(
                name: "BusinessCalendarEvents");
        }
    }
}
