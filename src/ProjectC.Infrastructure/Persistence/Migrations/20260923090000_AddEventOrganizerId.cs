using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventOrganizerId : Migration
    {
        // 見 event-management-organizer-scoping design.md Migration Plan：僅供本次遷移內部識別使用的固定 GUID，
        // 與一般 Organizer.Id 的 Guid.NewGuid() 生成方式無關，不會與自助申請產生的 Organizer 碰撞。
        private const string LegacyOrganizerId = "11111111-1111-1111-1111-111111111111";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 3.1：先加可為 null 的欄位，不加 NOT NULL／外鍵約束，讓下面的回填先在既有資料上執行。
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizerId",
                table: "Events",
                type: "uuid",
                nullable: true);

            // 3.2：以固定 GUID 建立「既有資料轉入」用 Organizer，依 Id 衝突即略過插入（冪等）。
            // CreatedByMemberId 借用目前任一既有 MemberRole.Admin（Role = 1）成員；沒有任何 Admin 時這筆
            // Organizer 不會被建立，由下方 3.2b 前的明確檢查擋下（不假設「沒有 Admin 就沒有 Event」——
            // Admin 角色是手動設定的，CreatedByMemberId 上線前的舊活動也沒有建立者可以反推）。
            migrationBuilder.Sql($"""
                INSERT INTO "Organizers" ("Id", "Name", "Status", "CreatedByMemberId", "CreatedAtUtc")
                SELECT '{LegacyOrganizerId}', '既有資料轉入', 1, "Id", now()
                FROM "Members"
                WHERE "Role" = 1
                ORDER BY "Id"
                LIMIT 1
                ON CONFLICT ("Id") DO NOTHING;
                """);

            // 3.2a：為所有既有 MemberRole.Admin 成員各自建立一筆掛在轉入用 Organizer 下的 OrganizerMember
            // （Role = 0 即 Owner），依 (OrganizerId, MemberId) 複合唯一索引衝突即略過插入（冪等）。
            migrationBuilder.Sql($"""
                INSERT INTO "OrganizerMembers" ("Id", "OrganizerId", "MemberId", "Role")
                SELECT gen_random_uuid(), '{LegacyOrganizerId}', "Id", 0
                FROM "Members"
                WHERE "Role" = 1
                ON CONFLICT ("OrganizerId", "MemberId") DO NOTHING;
                """);

            // 有待回填的 Event、但轉入用 Organizer 不存在（資料庫沒有任何 Admin）時明確中止，不讓 3.2b
            // 把 Event 指向不存在的 Organizer，拖到下一支 migration 加外鍵時才以泛用 FK 錯誤失敗。
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Events" WHERE "OrganizerId" IS NULL)
                       AND NOT EXISTS (SELECT 1 FROM "Organizers" WHERE "Id" = '{LegacyOrganizerId}') THEN
                        RAISE EXCEPTION 'AddEventOrganizerId backfill check failed: '
                            'Events rows need backfilling but the legacy Organizer could not be created '
                            'because no Member with Role = Admin exists. Promote at least one Admin and retry.';
                    END IF;
                END $$;
                """);

            // 3.2b：回填既有 Event，WHERE 條件本身保證重跑時已回填過的列是不可變的 no-op（冪等）。
            migrationBuilder.Sql($"""
                UPDATE "Events"
                SET "OrganizerId" = '{LegacyOrganizerId}'
                WHERE "OrganizerId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback 為部署運維程序，非本次自動化測試範圍（見 design.md Migration Plan 步驟 6）：
            // 這裡只還原 schema，刻意不還原 3.2～3.2b 寫入的 Organizer／OrganizerMember／Event 回填資料。
            migrationBuilder.DropColumn(
                name: "OrganizerId",
                table: "Events");
        }
    }
}
