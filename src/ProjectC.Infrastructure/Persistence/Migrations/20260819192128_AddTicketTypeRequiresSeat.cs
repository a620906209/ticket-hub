using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketTypeRequiresSeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AvailableQuantity",
                table: "TicketTypes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresSeat",
                table: "TicketTypes",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "EventSeatId",
                table: "OrderItems",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "OrderItems",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "TicketTypeId",
                table: "OrderItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_TicketTypes_RequiresSeat_AvailableQuantity",
                table: "TicketTypes",
                sql: "(\"RequiresSeat\" = TRUE AND \"AvailableQuantity\" IS NULL) OR (\"RequiresSeat\" = FALSE AND \"AvailableQuantity\" >= 0)");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_TicketTypeId",
                table: "OrderItems",
                column: "TicketTypeId");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_TicketTypes_TicketTypeId",
                table: "OrderItems",
                column: "TicketTypeId",
                principalTable: "TicketTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 安全防護（外部審查抓到）：這支 migration 的 Down() 最後一步會把 OrderItems.EventSeatId
            // 改回 NOT NULL，並把既有 NULL 值（純計數行項）批次填成 Guid.Empty。但 EventSeatId 有 FK
            // 約束指向 EventSeats，Guid.Empty 不對應任何實際存在的座位，這個回填必定違反 FK、rollback
            // 必定失敗——而且是在已經 DROP 掉 TicketTypeId/Quantity 欄位「之後」才失敗，等於資料已經
            // 遺失但交易還沒 commit（transaction 內會整個 rollback，所以不會真的憑空遺失資料，但錯誤
            // 訊息會是難懂的 FK violation，不會講清楚真正原因）。這裡提早用明確訊息擋下來，
            // 而不是讓部署人員誤以為這是一支「純新增欄位、可安全 down」的 migration。
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "OrderItems" WHERE "EventSeatId" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot roll back migration 20260819192128_AddTicketTypeRequiresSeat: '
                            'one or more OrderItems rows have EventSeatId IS NULL (pure counting order items). '
                            'This column cannot be safely reverted to NOT NULL because there is no valid EventSeat '
                            'to backfill with (Guid.Empty would violate the EventSeatId foreign key). '
                            'A controlled data migration (e.g. cancelling/refunding those orders, or manually '
                            'deciding how to handle them) must be performed before this migration can be rolled back.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_TicketTypes_TicketTypeId",
                table: "OrderItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TicketTypes_RequiresSeat_AvailableQuantity",
                table: "TicketTypes");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_TicketTypeId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "AvailableQuantity",
                table: "TicketTypes");

            migrationBuilder.DropColumn(
                name: "RequiresSeat",
                table: "TicketTypes");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "TicketTypeId",
                table: "OrderItems");

            migrationBuilder.AlterColumn<Guid>(
                name: "EventSeatId",
                table: "OrderItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
