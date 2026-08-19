using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class RefundItemRestockDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RestockNote",
                table: "RefundItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RestockedQuantity",
                table: "RefundItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestockNote",
                table: "RefundItems");

            migrationBuilder.DropColumn(
                name: "RestockedQuantity",
                table: "RefundItems");
        }
    }
}
