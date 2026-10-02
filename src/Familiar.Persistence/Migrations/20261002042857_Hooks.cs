using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Familiar.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Hooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hook_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hook_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_hook_tokens_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mcp_servers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    protected_url = table.Column<string>(type: "text", nullable: false),
                    protected_token = table.Column<string>(type: "text", nullable: true),
                    tools_json = table.Column<string>(type: "jsonb", nullable: false),
                    enabled_tools = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_servers", x => x.id);
                    table.ForeignKey(
                        name: "fk_mcp_servers_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhooks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    protected_url = table.Column<string>(type: "text", nullable: false),
                    protected_secret = table.Column<string>(type: "text", nullable: false),
                    events = table.Column<List<string>>(type: "text[]", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_result = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhooks", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhooks_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hook_tokens_account_id",
                table: "hook_tokens",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_servers_account_id_name",
                table: "mcp_servers",
                columns: new[] { "account_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhooks_account_id",
                table: "webhooks",
                column: "account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hook_tokens");

            migrationBuilder.DropTable(
                name: "mcp_servers");

            migrationBuilder.DropTable(
                name: "webhooks");
        }
    }
}
