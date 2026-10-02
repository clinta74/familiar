using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Familiar.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AvatarCustomisation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "appearance",
                table: "familiar_profiles",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "avatar_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    meta = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_avatar_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_avatar_assets_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_avatar_assets_account_id",
                table: "avatar_assets",
                column: "account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "avatar_assets");

            migrationBuilder.DropColumn(
                name: "appearance",
                table: "familiar_profiles");
        }
    }
}
