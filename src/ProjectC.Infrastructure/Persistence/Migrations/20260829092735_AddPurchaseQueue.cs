using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsQueueModeEnabled",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PurchaseQueueEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AdmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AdmissionExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseQueueEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseQueueEntries_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseQueueEntries_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseQueueEntries_EventId_MemberId",
                table: "PurchaseQueueEntries",
                columns: new[] { "EventId", "MemberId" },
                unique: true,
                filter: "\"Status\" IN ('Waiting', 'Admitted')");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseQueueEntries_EventId_Status_JoinedAtUtc_Id",
                table: "PurchaseQueueEntries",
                columns: new[] { "EventId", "Status", "JoinedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseQueueEntries_MemberId",
                table: "PurchaseQueueEntries",
                column: "MemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseQueueEntries");

            migrationBuilder.DropColumn(
                name: "IsQueueModeEnabled",
                table: "Events");
        }
    }
}
