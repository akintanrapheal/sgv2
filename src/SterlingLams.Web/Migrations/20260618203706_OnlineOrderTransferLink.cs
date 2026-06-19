using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class OnlineOrderTransferLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrderId",
                table: "StockTransfers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_OrderId",
                table: "StockTransfers",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockTransfers_OrderId",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "StockTransfers");
        }
    }
}
