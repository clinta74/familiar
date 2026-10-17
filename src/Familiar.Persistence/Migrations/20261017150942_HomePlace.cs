using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Familiar.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HomePlace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "home_country_code",
                table: "familiar_profiles",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "home_latitude",
                table: "familiar_profiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "home_longitude",
                table: "familiar_profiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "home_region",
                table: "familiar_profiles",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "home_country_code",
                table: "familiar_profiles");

            migrationBuilder.DropColumn(
                name: "home_latitude",
                table: "familiar_profiles");

            migrationBuilder.DropColumn(
                name: "home_longitude",
                table: "familiar_profiles");

            migrationBuilder.DropColumn(
                name: "home_region",
                table: "familiar_profiles");
        }
    }
}
