using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 先加可為 null 的欄位，讓下面的回填在既有資料上執行；EF 預設產生的 NOT NULL + DateTime.MinValue
            // 預設值會讓既有訂單的建立時間變成 0001-01-01（admin-order-list-enrichment design.md 決策 2）。
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            // 原生 SQL 為 CLAUDE.md「禁止原生 SQL」的明確例外（比照 AddEventOrganizerId 的回填）：EF Core migration API
            // 沒有「以其他欄位運算更新」的操作；內容為固定字串、只在 migration 執行、不接受外部輸入。
            // 回填值精確：CreateOrderHandler 是唯一建單處，持有時間自第一版起固定 10 分鐘（design.md Context）。
            migrationBuilder.Sql("""
                UPDATE "Orders" SET "CreatedAtUtc" = "HeldUntilUtc" - INTERVAL '10 minutes';
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "Orders");
        }
    }
}
