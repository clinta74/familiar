using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Familiar.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceCalendars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "calendar_sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_sources_device_id",
                table: "calendar_sources",
                column: "device_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_calendar_sources_access_tokens_device_id",
                table: "calendar_sources",
                column: "device_id",
                principalTable: "access_tokens",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_calendar_sources_access_tokens_device_id",
                table: "calendar_sources");

            migrationBuilder.DropIndex(
                name: "ix_calendar_sources_device_id",
                table: "calendar_sources");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "calendar_sources");
        }
    }
}
