using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceEventOrganizerIdNotNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 3.3：MUST 在套用 NOT NULL 約束前，以明確、可辨識的檢查主動偵測殘留 OrganizerId IS NULL 的列
            // （見 design.md「部署窗口風險」）。訊息可辨識為此次殘留檢查本身，不依賴資料庫底層 NOT NULL
            // 違反錯誤碰巧失敗。EF Core 預設把整支 migration 的 Up() 包在同一個交易內，中止時整支
            // migration（含下方的約束、外鍵）完全不套用，不產生部分套用的 schema 變更。
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Events" WHERE "OrganizerId" IS NULL) THEN
                        RAISE EXCEPTION 'EnforceEventOrganizerIdNotNull preflight check failed: '
                            'one or more Events rows have OrganizerId IS NULL. '
                            'Run the AddEventOrganizerId migration''s backfill again before retrying this migration.';
                    END IF;
                    -- 同樣在加外鍵前明確偵測指向不存在 Organizer 的孤兒列，不依賴 AddForeignKey 碰巧以泛用 FK 錯誤失敗
                    -- （涵蓋 AddEventOrganizerId 在加入其自身檢查前就已套用過的資料庫）。
                    IF EXISTS (
                        SELECT 1 FROM "Events" e
                        WHERE NOT EXISTS (SELECT 1 FROM "Organizers" o WHERE o."Id" = e."OrganizerId")) THEN
                        RAISE EXCEPTION 'EnforceEventOrganizerIdNotNull preflight check failed: '
                            'one or more Events rows reference an OrganizerId that does not exist in Organizers.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizerId",
                table: "Events",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_OrganizerId",
                table: "Events",
                column: "OrganizerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Events_Organizers_OrganizerId",
                table: "Events",
                column: "OrganizerId",
                principalTable: "Organizers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Events_Organizers_OrganizerId",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_OrganizerId",
                table: "Events");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizerId",
                table: "Events",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }
    }
}
