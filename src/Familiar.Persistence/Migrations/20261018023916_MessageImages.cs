using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Familiar.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MessageImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "image_id",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "message_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_message_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_message_images_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_messages_image_id",
                table: "messages",
                column: "image_id");

            migrationBuilder.CreateIndex(
                name: "ix_message_images_account_id_created_at",
                table: "message_images",
                columns: new[] { "account_id", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_messages_message_images_image_id",
                table: "messages",
                column: "image_id",
                principalTable: "message_images",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_messages_message_images_image_id",
                table: "messages");

            migrationBuilder.DropTable(
                name: "message_images");

            migrationBuilder.DropIndex(
                name: "ix_messages_image_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "image_id",
                table: "messages");
        }
    }
}
