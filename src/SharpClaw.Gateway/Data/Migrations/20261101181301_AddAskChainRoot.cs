using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAskChainRoot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "chain_root_id",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_messages_chain_root_id",
                table: "messages",
                column: "chain_root_id",
                filter: "chain_root_id IS NOT NULL");

            // Existing asks: a hop-1 ask's root is the human message it came from; deeper ones
            // inherit their parent's root (at most AgentMessaging:MaxHops, i.e. 10, levels).
            migrationBuilder.Sql("UPDATE messages SET chain_root_id = origin_message_id WHERE hop = 1;");
            for (var hop = 2; hop <= 10; hop++)
                migrationBuilder.Sql($@"UPDATE messages SET chain_root_id =
                    (SELECT p.chain_root_id FROM messages p WHERE p.id = messages.origin_message_id)
                    WHERE hop = {hop};");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_chain_root_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "chain_root_id",
                table: "messages");
        }
    }
}
