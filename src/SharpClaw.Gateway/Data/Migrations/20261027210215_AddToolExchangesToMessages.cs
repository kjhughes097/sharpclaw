using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddToolExchangesToMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_correlation_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_messages_thread_id_created_at",
                table: "messages");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "messages",
                type: "TEXT",
                nullable: false,
                defaultValue: "message");

            migrationBuilder.AddColumn<int>(
                name: "seq",
                table: "messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "tool_call_id",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tool_calls",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "tool_ok",
                table: "messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "truncated",
                table: "messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "untrusted",
                table: "messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Backfill seq per thread in the old (created_at, id) order before the unique index exists.
            migrationBuilder.Sql("""
                UPDATE messages SET seq = (
                    SELECT COUNT(*) FROM messages m2
                    WHERE m2.thread_id = messages.thread_id
                      AND (m2.created_at < messages.created_at
                           OR (m2.created_at = messages.created_at AND m2.id <= messages.id)));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_messages_correlation_id",
                table: "messages",
                column: "correlation_id",
                unique: true,
                filter: "role = 'assistant' AND kind = 'message'");

            migrationBuilder.CreateIndex(
                name: "IX_messages_thread_id_seq",
                table: "messages",
                columns: new[] { "thread_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_correlation_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "IX_messages_thread_id_seq",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "seq",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "tool_call_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "tool_calls",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "tool_ok",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "truncated",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "untrusted",
                table: "messages");

            migrationBuilder.CreateIndex(
                name: "IX_messages_correlation_id",
                table: "messages",
                column: "correlation_id",
                unique: true,
                filter: "role = 'assistant'");

            migrationBuilder.CreateIndex(
                name: "IX_messages_thread_id_created_at",
                table: "messages",
                columns: new[] { "thread_id", "created_at" });
        }
    }
}
