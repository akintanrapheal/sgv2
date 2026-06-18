using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class TransferReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferItems_StageQty_NonNegative",
                table: "StockTransferItems");

            migrationBuilder.AddColumn<int>(
                name: "DamagedQty",
                table: "StockTransferItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WontFulfilQty",
                table: "StockTransferItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferItems_StageQty_NonNegative",
                table: "StockTransferItems",
                sql: "(\"ApprovedQty\" IS NULL OR \"ApprovedQty\" >= 0) AND (\"DispatchedQty\" IS NULL OR \"DispatchedQty\" >= 0) AND (\"ReceivedQty\" IS NULL OR \"ReceivedQty\" >= 0) AND (\"DamagedQty\" IS NULL OR \"DamagedQty\" >= 0) AND (\"WontFulfilQty\" IS NULL OR \"WontFulfilQty\" >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferItems_StageQty_NonNegative",
                table: "StockTransferItems");

            migrationBuilder.DropColumn(
                name: "DamagedQty",
                table: "StockTransferItems");

            migrationBuilder.DropColumn(
                name: "WontFulfilQty",
                table: "StockTransferItems");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferItems_StageQty_NonNegative",
                table: "StockTransferItems",
                sql: "(\"ApprovedQty\" IS NULL OR \"ApprovedQty\" >= 0) AND (\"DispatchedQty\" IS NULL OR \"DispatchedQty\" >= 0) AND (\"ReceivedQty\" IS NULL OR \"ReceivedQty\" >= 0)");
        }
    }
}
