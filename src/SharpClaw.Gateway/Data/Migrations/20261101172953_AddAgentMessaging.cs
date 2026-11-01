using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "origin_agent_id",
                table: "threads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "origin_thread_id",
                table: "threads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "hop",
                table: "messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "origin_message_id",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "origin_tainted",
                table: "messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "origin_tool_call_id",
                table: "messages",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sender_agent_id",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_threads_agent_id_origin_agent_id_origin_thread_id",
                table: "threads",
                columns: new[] { "agent_id", "origin_agent_id", "origin_thread_id" },
                unique: true,
                filter: "origin_thread_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_messages_origin_message_id_origin_tool_call_id",
                table: "messages",
                columns: new[] { "origin_message_id", "origin_tool_call_id" },
                unique: true,
                filter: "origin_message_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_threads_agent_id_origin_agent_id_origin_thread_id",
                table: "threads");

            migrationBuilder.DropIndex(
                name: "IX_messages_origin_message_id_origin_tool_call_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "origin_agent_id",
                table: "threads");

            migrationBuilder.DropColumn(
                name: "origin_thread_id",
                table: "threads");

            migrationBuilder.DropColumn(
                name: "hop",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "origin_message_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "origin_tainted",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "origin_tool_call_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "sender_agent_id",
                table: "messages");
        }
    }
}
