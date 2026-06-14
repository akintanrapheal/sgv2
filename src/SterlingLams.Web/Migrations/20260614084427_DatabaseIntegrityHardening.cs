using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseIntegrityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_StoreInventories_OnHand_NonNegative",
                table: "StoreInventories",
                sql: "\"QuantityOnHand\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StoreInventories_Reserved_NonNegative",
                table: "StoreInventories",
                sql: "\"QuantityReserved\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CreatedAt",
                table: "StockTransfers",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_Status",
                table: "StockTransfers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_TransferNumber",
                table: "StockTransfers",
                column: "TransferNumber",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferItems_RequestedQty_Positive",
                table: "StockTransferItems",
                sql: "\"RequestedQty\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockTransferItems_StageQty_NonNegative",
                table: "StockTransferItems",
                sql: "(\"ApprovedQty\" IS NULL OR \"ApprovedQty\" >= 0) AND (\"DispatchedQty\" IS NULL OR \"DispatchedQty\" >= 0) AND (\"ReceivedQty\" IS NULL OR \"ReceivedQty\" >= 0)");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_CreatedAt",
                table: "StockReservations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_ProductId_StoreId",
                table: "StockReservations",
                columns: new[] { "ProductId", "StoreId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockReservations_Quantity_Positive",
                table: "StockReservations",
                sql: "\"Quantity\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_RefundNumber",
                table: "Refunds",
                column: "RefundNumber",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Refunds_Amount_NonNegative",
                table: "Refunds",
                sql: "\"Amount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_Barcode",
                table: "ProductVariants",
                column: "Barcode",
                unique: true,
                filter: "\"Barcode\" IS NOT NULL AND \"Barcode\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariants_Sku",
                table: "ProductVariants",
                column: "Sku");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Barcode",
                table: "Products",
                column: "Barcode",
                unique: true,
                filter: "\"Barcode\" IS NOT NULL AND \"Barcode\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Sku",
                table: "Products",
                column: "Sku");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_Price_NonNegative",
                table: "Products",
                sql: "\"Price\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Channel_CreatedAt",
                table: "Orders",
                columns: new[] { "Channel", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CreatedAt",
                table: "Orders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PaymentReference",
                table: "Orders",
                column: "PaymentReference",
                filter: "\"PaymentReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status",
                table: "Orders",
                column: "Status");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_Subtotal_NonNegative",
                table: "Orders",
                sql: "\"Subtotal\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Orders_Total_NonNegative",
                table: "Orders",
                sql: "\"Total\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_Discount_NonNegative",
                table: "OrderItems",
                sql: "\"DiscountAmount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_Quantity_Positive",
                table: "OrderItems",
                sql: "\"Quantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_UnitPrice_NonNegative",
                table: "OrderItems",
                sql: "\"UnitPrice\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action",
                table: "AuditLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedAt",
                table: "AuditLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType",
                table: "AuditLogs",
                column: "EntityType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StoreInventories_OnHand_NonNegative",
                table: "StoreInventories");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StoreInventories_Reserved_NonNegative",
                table: "StoreInventories");

            migrationBuilder.DropIndex(
                name: "IX_StockTransfers_CreatedAt",
                table: "StockTransfers");

            migrationBuilder.DropIndex(
                name: "IX_StockTransfers_Status",
                table: "StockTransfers");

            migrationBuilder.DropIndex(
                name: "IX_StockTransfers_TransferNumber",
                table: "StockTransfers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferItems_RequestedQty_Positive",
                table: "StockTransferItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockTransferItems_StageQty_NonNegative",
                table: "StockTransferItems");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_CreatedAt",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_ProductId_StoreId",
                table: "StockReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockReservations_Quantity_Positive",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_RefundNumber",
                table: "Refunds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Refunds_Amount_NonNegative",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_ProductVariants_Barcode",
                table: "ProductVariants");

            migrationBuilder.DropIndex(
                name: "IX_ProductVariants_Sku",
                table: "ProductVariants");

            migrationBuilder.DropIndex(
                name: "IX_Products_Barcode",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_Sku",
                table: "Products");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Products_Price_NonNegative",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Orders_Channel_CreatedAt",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CreatedAt",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_PaymentReference",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_Status",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_Subtotal_NonNegative",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Orders_Total_NonNegative",
                table: "Orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_Discount_NonNegative",
                table: "OrderItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_Quantity_Positive",
                table: "OrderItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_UnitPrice_NonNegative",
                table: "OrderItems");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Action",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_CreatedAt",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_EntityType",
                table: "AuditLogs");
        }
    }
}
