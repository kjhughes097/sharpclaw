using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddThreadModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "threads",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "model",
                table: "threads");
        }
    }
}
