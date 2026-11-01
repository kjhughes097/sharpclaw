using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    message_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    thread_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    media_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    data = table.Column<byte[]>(type: "BLOB", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_attachments", x => x.id);
                    table.ForeignKey(
                        name: "FK_message_attachments_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_message_attachments_message_id",
                table: "message_attachments",
                column: "message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_attachments");
        }
    }
}
