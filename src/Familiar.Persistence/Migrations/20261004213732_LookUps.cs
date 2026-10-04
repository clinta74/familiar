using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Familiar.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LookUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "home_place",
                table: "familiar_profiles",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "look_ups",
                table: "familiar_profiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "home_place",
                table: "familiar_profiles");

            migrationBuilder.DropColumn(
                name: "look_ups",
                table: "familiar_profiles");
        }
    }
}
