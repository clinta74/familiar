using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Familiar.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhonePushAndRingingReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ring",
                table: "scheduled_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "push_subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_push_subscriptions_device_id",
                table: "push_subscriptions",
                column: "device_id");

            migrationBuilder.AddForeignKey(
                name: "fk_push_subscriptions_access_tokens_device_id",
                table: "push_subscriptions",
                column: "device_id",
                principalTable: "access_tokens",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_push_subscriptions_access_tokens_device_id",
                table: "push_subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_push_subscriptions_device_id",
                table: "push_subscriptions");

            migrationBuilder.DropColumn(
                name: "ring",
                table: "scheduled_jobs");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "push_subscriptions");
        }
    }
}
