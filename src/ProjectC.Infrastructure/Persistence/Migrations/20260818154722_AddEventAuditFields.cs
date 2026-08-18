using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByMemberId",
                table: "Events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_CreatedByMemberId",
                table: "Events",
                column: "CreatedByMemberId");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_Members_CreatedByMemberId",
                table: "Events",
                column: "CreatedByMemberId",
                principalTable: "Members",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_Members_CreatedByMemberId",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_CreatedByMemberId",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CreatedByMemberId",
                table: "Events");
        }
    }
}
