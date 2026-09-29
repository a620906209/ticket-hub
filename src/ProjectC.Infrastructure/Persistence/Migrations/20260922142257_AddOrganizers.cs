using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizerId",
                table: "RefreshTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Organizers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedByMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedByMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Organizers_Members_CreatedByMemberId",
                        column: x => x.CreatedByMemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Organizers_Members_ReviewedByMemberId",
                        column: x => x.ReviewedByMemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrganizerMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizerId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizerMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizerMembers_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrganizerMembers_Organizers_OrganizerId",
                        column: x => x.OrganizerId,
                        principalTable: "Organizers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_OrganizerId",
                table: "RefreshTokens",
                column: "OrganizerId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizerMembers_MemberId",
                table: "OrganizerMembers",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizerMembers_OrganizerId_MemberId",
                table: "OrganizerMembers",
                columns: new[] { "OrganizerId", "MemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Organizers_CreatedByMemberId",
                table: "Organizers",
                column: "CreatedByMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizers_ReviewedByMemberId",
                table: "Organizers",
                column: "ReviewedByMemberId");

            migrationBuilder.AddForeignKey(
                name: "FK_RefreshTokens_Organizers_OrganizerId",
                table: "RefreshTokens",
                column: "OrganizerId",
                principalTable: "Organizers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RefreshTokens_Organizers_OrganizerId",
                table: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "OrganizerMembers");

            migrationBuilder.DropTable(
                name: "Organizers");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_OrganizerId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "OrganizerId",
                table: "RefreshTokens");
        }
    }
}
