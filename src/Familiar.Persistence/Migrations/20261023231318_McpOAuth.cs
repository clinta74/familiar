using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Familiar.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpOAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "protected_oauth",
                table: "mcp_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sign_in_needed",
                table: "mcp_servers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "protected_oauth",
                table: "mcp_servers");

            migrationBuilder.DropColumn(
                name: "sign_in_needed",
                table: "mcp_servers");
        }
    }
}
