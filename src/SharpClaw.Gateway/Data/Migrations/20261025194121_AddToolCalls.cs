using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddToolCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tool_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    thread_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    correlation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    llm_call_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tool_call_id = table.Column<string>(type: "TEXT", nullable: false),
                    tool_name = table.Column<string>(type: "TEXT", nullable: false),
                    arguments = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    decision = table.Column<string>(type: "TEXT", nullable: true),
                    risk = table.Column<string>(type: "TEXT", nullable: true),
                    tainted = table.Column<bool>(type: "INTEGER", nullable: false),
                    approval_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", nullable: true),
                    duration_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    reported_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_calls", x => x.id);
                    table.ForeignKey(
                        name: "FK_tool_calls_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tool_calls_agent_id_created_at",
                table: "tool_calls",
                columns: new[] { "agent_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_calls_correlation_id_tool_call_id",
                table: "tool_calls",
                columns: new[] { "correlation_id", "tool_call_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tool_calls");
        }
    }
}
