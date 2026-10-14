using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferReturnedAndCompletionReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompletionReason",
                table: "StockTransfers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReturnedQty",
                table: "StockTransferItems",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionReason",
                table: "StockTransfers");

            migrationBuilder.DropColumn(
                name: "ReturnedQty",
                table: "StockTransferItems");
        }
    }
}
