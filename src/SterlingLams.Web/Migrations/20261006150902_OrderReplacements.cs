using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class OrderReplacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderReplacements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReplacementNumber = table.Column<string>(type: "text", nullable: false),
                    OriginalOrderId = table.Column<int>(type: "integer", nullable: false),
                    StoreId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    OldProductId = table.Column<int>(type: "integer", nullable: false),
                    OldProductVariantId = table.Column<int>(type: "integer", nullable: true),
                    OldProductName = table.Column<string>(type: "text", nullable: false),
                    OldVariantName = table.Column<string>(type: "text", nullable: true),
                    OldQuantity = table.Column<int>(type: "integer", nullable: false),
                    NewProductId = table.Column<int>(type: "integer", nullable: false),
                    NewProductVariantId = table.Column<int>(type: "integer", nullable: true),
                    NewProductName = table.Column<string>(type: "text", nullable: false),
                    NewVariantName = table.Column<string>(type: "text", nullable: true),
                    NewQuantity = table.Column<int>(type: "integer", nullable: false),
                    BalancePaid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BalanceNote = table.Column<string>(type: "text", nullable: true),
                    RestockDecision = table.Column<int>(type: "integer", nullable: false),
                    RestockedQuantity = table.Column<int>(type: "integer", nullable: false),
                    RestockNote = table.Column<string>(type: "text", nullable: true),
                    RestockDecidedByUserId = table.Column<string>(type: "text", nullable: true),
                    RestockDecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true),
                    CreatedByName = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderReplacements", x => x.Id);
                    table.CheckConstraint("CK_OrderReplacements_Balance_NonNegative", "\"BalancePaid\" >= 0");
                    table.ForeignKey(
                        name: "FK_OrderReplacements_Orders_OriginalOrderId",
                        column: x => x.OriginalOrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderReplacements_OriginalOrderId",
                table: "OrderReplacements",
                column: "OriginalOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderReplacements_ReplacementNumber",
                table: "OrderReplacements",
                column: "ReplacementNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderReplacements");
        }
    }
}
