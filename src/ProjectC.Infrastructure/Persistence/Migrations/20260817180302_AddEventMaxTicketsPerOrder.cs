using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProjectC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventMaxTicketsPerOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxTicketsPerOrder",
                table: "Events",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxTicketsPerOrder",
                table: "Events");
        }
    }
}
