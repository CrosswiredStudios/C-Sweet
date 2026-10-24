using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChiefLeadershipOrganizationIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ChiefFocusPrompts_OrganizationId",
                table: "ChiefFocusPrompts",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChiefCoverageRecords_CoreOrganizations_OrganizationId",
                table: "ChiefCoverageRecords",
                column: "OrganizationId",
                principalTable: "CoreOrganizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChiefFocusPrompts_CoreOrganizations_OrganizationId",
                table: "ChiefFocusPrompts",
                column: "OrganizationId",
                principalTable: "CoreOrganizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChiefFocusRecords_CoreOrganizations_OrganizationId",
                table: "ChiefFocusRecords",
                column: "OrganizationId",
                principalTable: "CoreOrganizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChiefLeadershipAgendas_CoreOrganizations_Id",
                table: "ChiefLeadershipAgendas",
                column: "Id",
                principalTable: "CoreOrganizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChiefLeadershipReceipts_CoreOrganizations_OrganizationId",
                table: "ChiefLeadershipReceipts",
                column: "OrganizationId",
                principalTable: "CoreOrganizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChiefCoverageRecords_CoreOrganizations_OrganizationId",
                table: "ChiefCoverageRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_ChiefFocusPrompts_CoreOrganizations_OrganizationId",
                table: "ChiefFocusPrompts");

            migrationBuilder.DropForeignKey(
                name: "FK_ChiefFocusRecords_CoreOrganizations_OrganizationId",
                table: "ChiefFocusRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_ChiefLeadershipAgendas_CoreOrganizations_Id",
                table: "ChiefLeadershipAgendas");

            migrationBuilder.DropForeignKey(
                name: "FK_ChiefLeadershipReceipts_CoreOrganizations_OrganizationId",
                table: "ChiefLeadershipReceipts");

            migrationBuilder.DropIndex(
                name: "IX_ChiefFocusPrompts_OrganizationId",
                table: "ChiefFocusPrompts");
        }
    }
}
