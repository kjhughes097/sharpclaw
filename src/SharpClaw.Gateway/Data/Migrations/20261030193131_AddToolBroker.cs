using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddToolBroker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "args_hash",
                table: "approvals",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "used_at",
                table: "approvals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "broker_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    thread_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    correlation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tool_call_id = table.Column<string>(type: "TEXT", nullable: false),
                    tool = table.Column<string>(type: "TEXT", nullable: false),
                    target_host = table.Column<string>(type: "TEXT", nullable: true),
                    target = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    error_code = table.Column<string>(type: "TEXT", nullable: true),
                    bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    approval_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_broker_calls", x => x.id);
                    table.ForeignKey(
                        name: "FK_broker_calls_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_broker_calls_agent_id_created_at",
                table: "broker_calls",
                columns: new[] { "agent_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_broker_calls_correlation_id",
                table: "broker_calls",
                column: "correlation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "broker_calls");

            migrationBuilder.DropColumn(
                name: "args_hash",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "used_at",
                table: "approvals");
        }
    }
}
