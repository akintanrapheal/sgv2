using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class VariantLevelStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoreInventories_ProductId_StoreId",
                table: "StoreInventories");

            migrationBuilder.AddColumn<int>(
                name: "ProductVariantId",
                table: "StoreInventories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProductVariantId",
                table: "StockReservations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoreInventories_ProductId_StoreId",
                table: "StoreInventories",
                columns: new[] { "ProductId", "StoreId" },
                unique: true,
                filter: "\"ProductVariantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StoreInventories_ProductId_StoreId_ProductVariantId",
                table: "StoreInventories",
                columns: new[] { "ProductId", "StoreId", "ProductVariantId" },
                unique: true,
                filter: "\"ProductVariantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StoreInventories_ProductVariantId",
                table: "StoreInventories",
                column: "ProductVariantId");

            migrationBuilder.AddForeignKey(
                name: "FK_StoreInventories_ProductVariants_ProductVariantId",
                table: "StoreInventories",
                column: "ProductVariantId",
                principalTable: "ProductVariants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StoreInventories_ProductVariants_ProductVariantId",
                table: "StoreInventories");

            migrationBuilder.DropIndex(
                name: "IX_StoreInventories_ProductId_StoreId",
                table: "StoreInventories");

            migrationBuilder.DropIndex(
                name: "IX_StoreInventories_ProductId_StoreId_ProductVariantId",
                table: "StoreInventories");

            migrationBuilder.DropIndex(
                name: "IX_StoreInventories_ProductVariantId",
                table: "StoreInventories");

            migrationBuilder.DropColumn(
                name: "ProductVariantId",
                table: "StoreInventories");

            migrationBuilder.DropColumn(
                name: "ProductVariantId",
                table: "StockReservations");

            migrationBuilder.CreateIndex(
                name: "IX_StoreInventories_ProductId_StoreId",
                table: "StoreInventories",
                columns: new[] { "ProductId", "StoreId" },
                unique: true);
        }
    }
}
