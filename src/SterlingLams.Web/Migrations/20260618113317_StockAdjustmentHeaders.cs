using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class StockAdjustmentHeaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockAdjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdjustmentNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StoreId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockAdjustments_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockAdjustmentLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockAdjustmentId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    ProductVariantId = table.Column<int>(type: "integer", nullable: true),
                    ProductName = table.Column<string>(type: "text", nullable: false),
                    VariantName = table.Column<string>(type: "text", nullable: true),
                    QtyDelta = table.Column<int>(type: "integer", nullable: false),
                    BalanceAfter = table.Column<int>(type: "integer", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric", nullable: true),
                    ExpiryDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustmentLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockAdjustmentLines_ProductVariants_ProductVariantId",
                        column: x => x.ProductVariantId,
                        principalTable: "ProductVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StockAdjustmentLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockAdjustmentLines_StockAdjustments_StockAdjustmentId",
                        column: x => x.StockAdjustmentId,
                        principalTable: "StockAdjustments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustmentLines_ProductId",
                table: "StockAdjustmentLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustmentLines_ProductVariantId",
                table: "StockAdjustmentLines",
                column: "ProductVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustmentLines_StockAdjustmentId",
                table: "StockAdjustmentLines",
                column: "StockAdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_AdjustmentNumber",
                table: "StockAdjustments",
                column: "AdjustmentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_CreatedAt",
                table: "StockAdjustments",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_StoreId",
                table: "StockAdjustments",
                column: "StoreId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockAdjustmentLines");

            migrationBuilder.DropTable(
                name: "StockAdjustments");
        }
    }
}
