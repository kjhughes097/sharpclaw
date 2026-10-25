using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    daily_budget_usd = table.Column<double>(type: "REAL", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "llm_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    thread_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    correlation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    prompt_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    completion_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    cost_usd = table.Column<double>(type: "REAL", nullable: false),
                    usage_known = table.Column<bool>(type: "INTEGER", nullable: false),
                    latency_ms = table.Column<int>(type: "INTEGER", nullable: false),
                    ttft_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", nullable: true),
                    error_detail = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_calls", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    thread_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    correlation_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    claimed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    completed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    fail_reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    prefix = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_tokens_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_threads", x => x.id);
                    table.ForeignKey(
                        name: "FK_threads_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_tokens_agent_id",
                table: "agent_tokens",
                column: "agent_id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_tokens_token_hash",
                table: "agent_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_name",
                table: "agents",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_llm_calls_agent_id_created_at",
                table: "llm_calls",
                columns: new[] { "agent_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_llm_calls_correlation_id",
                table: "llm_calls",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_messages_agent_id_role_status_created_at",
                table: "messages",
                columns: new[] { "agent_id", "role", "status", "created_at" });

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

            migrationBuilder.CreateIndex(
                name: "IX_threads_agent_id",
                table: "threads",
                column: "agent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_tokens");

            migrationBuilder.DropTable(
                name: "llm_calls");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "threads");

            migrationBuilder.DropTable(
                name: "agents");
        }
    }
}
