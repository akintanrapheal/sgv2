using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class StoreInventoryMinMaxOnOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxStock",
                table: "StoreInventories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinStock",
                table: "StoreInventories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OnOrder",
                table: "StoreInventories",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "StockAlerts",
                table: "StoreInventories",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxStock",
                table: "StoreInventories");

            migrationBuilder.DropColumn(
                name: "MinStock",
                table: "StoreInventories");

            migrationBuilder.DropColumn(
                name: "OnOrder",
                table: "StoreInventories");

            migrationBuilder.DropColumn(
                name: "StockAlerts",
                table: "StoreInventories");
        }
    }
}
