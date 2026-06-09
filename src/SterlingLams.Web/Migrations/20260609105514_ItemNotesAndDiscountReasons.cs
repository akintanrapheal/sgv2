using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class ItemNotesAndDiscountReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Stores_ErpNextWarehouse",
                table: "Stores");

            migrationBuilder.DropIndex(
                name: "IX_Products_ErpNextItemCode",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "Finish",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "Size",
                table: "ProductVariants");

            migrationBuilder.RenameColumn(
                name: "OdooVariantId",
                table: "ProductVariants",
                newName: "StockQuantity");

            migrationBuilder.RenameColumn(
                name: "ErpNextSalesOrderName",
                table: "Orders",
                newName: "TrackingNumber");

            migrationBuilder.AlterColumn<decimal>(
                name: "PriceAdjustment",
                table: "ProductVariants",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductType",
                table: "Products",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "AmountTendered",
                table: "Orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ChangeGiven",
                table: "Orders",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "Orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "Orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "DiscountCode",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpNextInvoiceName",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RegisterId",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TillSessionId",
                table: "Orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "OrderItems",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "DiscountReason",
                table: "OrderItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscountType",
                table: "OrderItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ItemNote",
                table: "OrderItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductSku",
                table: "OrderItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FirstOrderOnly",
                table: "DiscountCodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsAutomatic",
                table: "DiscountCodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxUsesPerCustomer",
                table: "DiscountCodes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinimumQuantity",
                table: "DiscountCodes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "DiscountCodes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "AuditLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PinHash",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DiscountCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscountCodeId = table.Column<int>(type: "integer", nullable: false),
                    CategoryId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscountCategories_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiscountCategories_DiscountCodes_DiscountCodeId",
                        column: x => x.DiscountCodeId,
                        principalTable: "DiscountCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscountProducts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscountCodeId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscountProducts_DiscountCodes_DiscountCodeId",
                        column: x => x.DiscountCodeId,
                        principalTable: "DiscountCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DiscountProducts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PosDiscountReasons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosDiscountReasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductAttributes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductAttributes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Refunds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RefundNumber = table.Column<string>(type: "text", nullable: false),
                    OriginalOrderId = table.Column<int>(type: "integer", nullable: false),
                    RegisterId = table.Column<int>(type: "integer", nullable: true),
                    TillSessionId = table.Column<int>(type: "integer", nullable: true),
                    CashierUserId = table.Column<string>(type: "text", nullable: false),
                    RefundMethod = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Refunds_Orders_OriginalOrderId",
                        column: x => x.OriginalOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Registers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Registers_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleName = table.Column<string>(type: "text", nullable: false),
                    Section = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Group = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    ProductVariantId = table.Column<int>(type: "integer", nullable: true),
                    StoreId = table.Column<int>(type: "integer", nullable: false),
                    QuantityChange = table.Column<int>(type: "integer", nullable: false),
                    BalanceAfter = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockMovements_ProductVariants_ProductVariantId",
                        column: x => x.ProductVariantId,
                        principalTable: "ProductVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_StockMovements_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StockMovements_Stores_StoreId",
                        column: x => x.StoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransfers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TransferNumber = table.Column<string>(type: "text", nullable: false),
                    FromStoreId = table.Column<int>(type: "integer", nullable: false),
                    ToStoreId = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Stores_FromStoreId",
                        column: x => x.FromStoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Stores_ToStoreId",
                        column: x => x.ToStoreId,
                        principalTable: "Stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PosDiscountPresets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReasonId = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosDiscountPresets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PosDiscountPresets_PosDiscountReasons_ReasonId",
                        column: x => x.ReasonId,
                        principalTable: "PosDiscountReasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductAttributeValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttributeId = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    ColorHex = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductAttributeValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductAttributeValues_ProductAttributes_AttributeId",
                        column: x => x.AttributeId,
                        principalTable: "ProductAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefundItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RefundId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    ProductVariantId = table.Column<int>(type: "integer", nullable: true),
                    ProductName = table.Column<string>(type: "text", nullable: false),
                    VariantName = table.Column<string>(type: "text", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefundItems_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TillSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RegisterId = table.Column<int>(type: "integer", nullable: false),
                    OpenedByUserId = table.Column<string>(type: "text", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedByUserId = table.Column<string>(type: "text", nullable: true),
                    CountedCash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ClosingNote = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TillSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TillSessions_Registers_RegisterId",
                        column: x => x.RegisterId,
                        principalTable: "Registers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StockTransferId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    ProductVariantId = table.Column<int>(type: "integer", nullable: true),
                    ProductName = table.Column<string>(type: "text", nullable: false),
                    VariantName = table.Column<string>(type: "text", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockTransferItems_StockTransfers_StockTransferId",
                        column: x => x.StockTransferId,
                        principalTable: "StockTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductVariantAttributeValue",
                columns: table => new
                {
                    AttributeValuesId = table.Column<int>(type: "integer", nullable: false),
                    VariantsId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductVariantAttributeValue", x => new { x.AttributeValuesId, x.VariantsId });
                    table.ForeignKey(
                        name: "FK_ProductVariantAttributeValue_ProductAttributeValues_Attribu~",
                        column: x => x.AttributeValuesId,
                        principalTable: "ProductAttributeValues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductVariantAttributeValue_ProductVariants_VariantsId",
                        column: x => x.VariantsId,
                        principalTable: "ProductVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Stores_ErpNextWarehouse",
                table: "Stores",
                column: "ErpNextWarehouse",
                unique: true,
                filter: "\"ErpNextWarehouse\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Products_ErpNextItemCode",
                table: "Products",
                column: "ErpNextItemCode",
                unique: true,
                filter: "\"ErpNextItemCode\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_RegisterId",
                table: "Orders",
                column: "RegisterId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TillSessionId",
                table: "Orders",
                column: "TillSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountCategories_CategoryId",
                table: "DiscountCategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountCategories_DiscountCodeId",
                table: "DiscountCategories",
                column: "DiscountCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountProducts_DiscountCodeId",
                table: "DiscountProducts",
                column: "DiscountCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscountProducts_ProductId",
                table: "DiscountProducts",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PosDiscountPresets_ReasonId",
                table: "PosDiscountPresets",
                column: "ReasonId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductAttributes_Slug",
                table: "ProductAttributes",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductAttributeValues_AttributeId",
                table: "ProductAttributeValues",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariantAttributeValue_VariantsId",
                table: "ProductVariantAttributeValue",
                column: "VariantsId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundItems_RefundId",
                table: "RefundItems",
                column: "RefundId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_OriginalOrderId",
                table: "Refunds",
                column: "OriginalOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Registers_StoreId",
                table: "Registers",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_RoleName_Section",
                table: "RolePermissions",
                columns: new[] { "RoleName", "Section" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SiteSettings_Key",
                table: "SiteSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ProductId_StoreId_CreatedAt",
                table: "StockMovements",
                columns: new[] { "ProductId", "StoreId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ProductVariantId",
                table: "StockMovements",
                column: "ProductVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_StoreId",
                table: "StockMovements",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferItems_StockTransferId",
                table: "StockTransferItems",
                column: "StockTransferId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_FromStoreId",
                table: "StockTransfers",
                column: "FromStoreId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_ToStoreId",
                table: "StockTransfers",
                column: "ToStoreId");

            migrationBuilder.CreateIndex(
                name: "IX_TillSessions_RegisterId_ClosedAt",
                table: "TillSessions",
                columns: new[] { "RegisterId", "ClosedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Registers_RegisterId",
                table: "Orders",
                column: "RegisterId",
                principalTable: "Registers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_TillSessions_TillSessionId",
                table: "Orders",
                column: "TillSessionId",
                principalTable: "TillSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Registers_RegisterId",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_TillSessions_TillSessionId",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "DiscountCategories");

            migrationBuilder.DropTable(
                name: "DiscountProducts");

            migrationBuilder.DropTable(
                name: "PosDiscountPresets");

            migrationBuilder.DropTable(
                name: "ProductVariantAttributeValue");

            migrationBuilder.DropTable(
                name: "RefundItems");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "SiteSettings");

            migrationBuilder.DropTable(
                name: "StockMovements");

            migrationBuilder.DropTable(
                name: "StockTransferItems");

            migrationBuilder.DropTable(
                name: "TillSessions");

            migrationBuilder.DropTable(
                name: "PosDiscountReasons");

            migrationBuilder.DropTable(
                name: "ProductAttributeValues");

            migrationBuilder.DropTable(
                name: "Refunds");

            migrationBuilder.DropTable(
                name: "StockTransfers");

            migrationBuilder.DropTable(
                name: "Registers");

            migrationBuilder.DropTable(
                name: "ProductAttributes");

            migrationBuilder.DropIndex(
                name: "IX_Stores_ErpNextWarehouse",
                table: "Stores");

            migrationBuilder.DropIndex(
                name: "IX_Products_ErpNextItemCode",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Orders_RegisterId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_TillSessionId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ProductType",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "AmountTendered",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ChangeGiven",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DiscountCode",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ErpNextInvoiceName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RegisterId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TillSessionId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "DiscountReason",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "DiscountType",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ItemNote",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ProductSku",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "FirstOrderOnly",
                table: "DiscountCodes");

            migrationBuilder.DropColumn(
                name: "IsAutomatic",
                table: "DiscountCodes");

            migrationBuilder.DropColumn(
                name: "MaxUsesPerCustomer",
                table: "DiscountCodes");

            migrationBuilder.DropColumn(
                name: "MinimumQuantity",
                table: "DiscountCodes");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "DiscountCodes");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "PinHash",
                table: "AspNetUsers");

            migrationBuilder.RenameColumn(
                name: "StockQuantity",
                table: "ProductVariants",
                newName: "OdooVariantId");

            migrationBuilder.RenameColumn(
                name: "TrackingNumber",
                table: "Orders",
                newName: "ErpNextSalesOrderName");

            migrationBuilder.AlterColumn<decimal>(
                name: "PriceAdjustment",
                table: "ProductVariants",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "ProductVariants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Finish",
                table: "ProductVariants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Size",
                table: "ProductVariants",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Stores_ErpNextWarehouse",
                table: "Stores",
                column: "ErpNextWarehouse",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_ErpNextItemCode",
                table: "Products",
                column: "ErpNextItemCode",
                unique: true);
        }
    }
}
