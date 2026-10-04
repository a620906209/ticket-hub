using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventSalesWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SalesEndAtUtc",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SalesStartAtUtc",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // CLAUDE.md「禁止原生 SQL」的明確例外（比照 AddRealNameVerification）：EF Core migration API 沒有「依資料狀態中止」的功能，
            // 直接刪欄會讓主辦方設定的販售期間靜默消失，重新升版後這些活動變成立即可售。
            // 只在 migration 內執行、內容是固定字串、不接受任何外部輸入（event-sales-window design.md 決策 7）。
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Events" WHERE "SalesStartAtUtc" IS NOT NULL OR "SalesEndAtUtc" IS NOT NULL) THEN
                        RAISE EXCEPTION 'AddEventSalesWindow Down aborted: events with sales window exist. Back up and clear the data before rolling back.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "SalesEndAtUtc",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "SalesStartAtUtc",
                table: "Events");
        }
    }
}
