using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Familiar.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationsAndFamiliars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                    table.ForeignKey(
                        name: "fk_conversations_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "familiar_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    user_nickname = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    warmth = table.Column<double>(type: "double precision", nullable: false),
                    playfulness = table.Column<double>(type: "double precision", nullable: false),
                    verbosity = table.Column<double>(type: "double precision", nullable: false),
                    formality = table.Column<double>(type: "double precision", nullable: false),
                    expressiveness = table.Column<double>(type: "double precision", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_familiar_profiles", x => x.id);
                    table.ForeignKey(
                        name: "fk_familiar_profiles_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    emotion = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    emotion_intensity = table.Column<double>(type: "double precision", nullable: true),
                    interrupted = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_messages_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_account_id_last_message_at",
                table: "conversations",
                columns: new[] { "account_id", "last_message_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_familiar_profiles_account_id",
                table: "familiar_profiles",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_conversation_id_id",
                table: "messages",
                columns: new[] { "conversation_id", "id" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "familiar_profiles");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "conversations");
        }
    }
}
