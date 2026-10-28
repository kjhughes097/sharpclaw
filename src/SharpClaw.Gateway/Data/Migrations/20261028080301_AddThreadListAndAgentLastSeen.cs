using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddThreadListAndAgentLastSeen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "archived",
                table: "threads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_message_at",
                table: "threads",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "last_preview",
                table: "threads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_seen_at",
                table: "agents",
                type: "TEXT",
                nullable: true);

            // Backfill from existing message rows (the thread's creation time when it has none). The
            // preview approximates the runtime whitespace collapse: line breaks and tabs become spaces.
            migrationBuilder.Sql("""
                UPDATE threads SET
                  last_message_at = COALESCE(
                    (SELECT m.created_at FROM messages m
                     WHERE m.thread_id = threads.id AND m.kind = 'message'
                     ORDER BY m.seq DESC LIMIT 1),
                    threads.created_at),
                  last_preview = (SELECT substr(trim(replace(replace(replace(m.content, char(13), ' '), char(10), ' '), char(9), ' ')), 1, 120)
                     FROM messages m
                     WHERE m.thread_id = threads.id AND m.kind = 'message'
                     ORDER BY m.seq DESC LIMIT 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_threads_archived_last_message_at",
                table: "threads",
                columns: new[] { "archived", "last_message_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_threads_archived_last_message_at",
                table: "threads");

            migrationBuilder.DropColumn(
                name: "archived",
                table: "threads");

            migrationBuilder.DropColumn(
                name: "last_message_at",
                table: "threads");

            migrationBuilder.DropColumn(
                name: "last_preview",
                table: "threads");

            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "agents");
        }
    }
}
