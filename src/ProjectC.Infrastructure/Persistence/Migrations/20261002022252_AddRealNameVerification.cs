using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRealNameVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NationalIdLast4",
                table: "Members",
                type: "character(4)",
                fixedLength: true,
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RealName",
                table: "Members",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRealNameRequired",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Members_RealName_NationalIdLast4",
                table: "Members",
                sql: "(\"RealName\" IS NULL) = (\"NationalIdLast4\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // CLAUDE.md「禁止原生 SQL」的明確例外：EF Core migration API 沒有「依資料狀態中止」的功能，
            // 而直接刪欄會讓已登記的實名與需實名活動設定靜默消失（重新升版後全部變成未登記／不需實名）。
            // 只在 migration 內執行、內容是固定字串、不接受任何外部輸入（real-name-verification design.md Migration Plan 第 3 點）。
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Members" WHERE "RealName" IS NOT NULL) THEN
                        RAISE EXCEPTION 'AddRealNameVerification Down aborted: members with registered real name exist. Back up and clear the data before rolling back.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Events" WHERE "IsRealNameRequired") THEN
                        RAISE EXCEPTION 'AddRealNameVerification Down aborted: real-name-required events exist. Back up and clear the data before rolling back.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Members_RealName_NationalIdLast4",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "NationalIdLast4",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "RealName",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "IsRealNameRequired",
                table: "Events");
        }
    }
}
