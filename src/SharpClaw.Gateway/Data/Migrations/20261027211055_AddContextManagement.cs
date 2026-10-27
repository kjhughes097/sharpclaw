using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContextManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "context_dropped_turns",
                table: "threads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "context_estimated_tokens",
                table: "threads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "estimated_prompt_tokens",
                table: "llm_calls",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "context_window_tokens",
                table: "agents",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "context_dropped_turns",
                table: "threads");

            migrationBuilder.DropColumn(
                name: "context_estimated_tokens",
                table: "threads");

            migrationBuilder.DropColumn(
                name: "estimated_prompt_tokens",
                table: "llm_calls");

            migrationBuilder.DropColumn(
                name: "context_window_tokens",
                table: "agents");
        }
    }
}
