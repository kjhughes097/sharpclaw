using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SharpClaw.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "detail",
                table: "approvals",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "detail",
                table: "approvals");
        }
    }
}
