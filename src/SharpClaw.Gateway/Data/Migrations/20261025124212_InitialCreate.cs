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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    DailyBudgetUsd = table.Column<double>(type: "REAL", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "llm_calls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ThreadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CostUsd = table.Column<double>(type: "REAL", nullable: false),
                    UsageKnown = table.Column<bool>(type: "INTEGER", nullable: false),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: false),
                    TtftMs = table.Column<int>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorDetail = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_calls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ThreadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agent_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_tokens_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "threads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_threads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_threads_agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_tokens_AgentId",
                table: "agent_tokens",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_tokens_TokenHash",
                table: "agent_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_Name",
                table: "agents",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_llm_calls_AgentId_CreatedAt",
                table: "llm_calls",
                columns: new[] { "AgentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_llm_calls_CorrelationId",
                table: "llm_calls",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_messages_AgentId_Role_Status_CreatedAt",
                table: "messages",
                columns: new[] { "AgentId", "Role", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_messages_CorrelationId",
                table: "messages",
                column: "CorrelationId",
                unique: true,
                filter: "role = 'assistant'");

            migrationBuilder.CreateIndex(
                name: "IX_messages_ThreadId_CreatedAt",
                table: "messages",
                columns: new[] { "ThreadId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_threads_AgentId",
                table: "threads",
                column: "AgentId");
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
