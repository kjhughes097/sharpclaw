using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddThreadEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "event_log_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    pruned_through = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_event_log_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "thread_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    thread_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    data = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_thread_events", x => x.id);
                });

            migrationBuilder.InsertData(
                table: "event_log_state",
                columns: new[] { "id", "pruned_through" },
                values: new object[] { 1, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_thread_events_created_at",
                table: "thread_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_thread_events_thread_id_id",
                table: "thread_events",
                columns: new[] { "thread_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "event_log_state");

            migrationBuilder.DropTable(
                name: "thread_events");
        }
    }
}
