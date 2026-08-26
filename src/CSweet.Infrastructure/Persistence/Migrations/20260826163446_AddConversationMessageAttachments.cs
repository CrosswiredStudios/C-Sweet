using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationMessageAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationMessageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessageAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationMessageAttachments_CoreConversationMessages_Mes~",
                        column: x => x.MessageId,
                        principalTable: "CoreConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationMessageAttachments_CoreConversations_Conversati~",
                        column: x => x.ConversationId,
                        principalTable: "CoreConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationMessageAttachments_MediaAssets_MediaAssetId",
                        column: x => x.MediaAssetId,
                        principalTable: "MediaAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageAttachments_ConversationId",
                table: "ConversationMessageAttachments",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageAttachments_MediaAssetId",
                table: "ConversationMessageAttachments",
                column: "MediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageAttachments_MessageId_MediaAssetId",
                table: "ConversationMessageAttachments",
                columns: new[] { "MessageId", "MediaAssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageAttachments_OrganizationId_ConversationId",
                table: "ConversationMessageAttachments",
                columns: new[] { "OrganizationId", "ConversationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMessageAttachments");
        }
    }
}
