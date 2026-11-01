using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalGrantsAndAutonomy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "grant_id",
                table: "approvals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "autonomous",
                table: "agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "approval_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    agent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tool_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    thread_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_by_user = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_approval_grants_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approval_grants_agent_id_tool_name",
                table: "approval_grants",
                columns: new[] { "agent_id", "tool_name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approval_grants");

            migrationBuilder.DropColumn(
                name: "grant_id",
                table: "approvals");

            migrationBuilder.DropColumn(
                name: "autonomous",
                table: "agents");
        }
    }
}
