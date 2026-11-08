using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameTokenExpiresAtToExpiresAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ExpiresAt",
                table: "RefreshTokens",
                newName: "ExpiresAtUtc");

            migrationBuilder.RenameColumn(
                name: "ExpiresAt",
                table: "PasswordResetTokens",
                newName: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ExpiresAtUtc",
                table: "RefreshTokens",
                newName: "ExpiresAt");

            migrationBuilder.RenameColumn(
                name: "ExpiresAtUtc",
                table: "PasswordResetTokens",
                newName: "ExpiresAt");
        }
    }
}
