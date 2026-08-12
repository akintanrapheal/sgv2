using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundRestockDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RestockDecidedAt",
                table: "RefundItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestockDecidedByUserId",
                table: "RefundItems",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RestockDecision",
                table: "RefundItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Items of already-Approved refunds had their stock returned at Finance approval under Step 1,
            // so they're already resolved — mark them Restocked (1) so they don't appear in the new
            // Inventory restock queue. (Status 1 = Approved.)
            migrationBuilder.Sql(@"UPDATE ""RefundItems"" SET ""RestockDecision"" = 1
                WHERE ""RefundId"" IN (SELECT ""Id"" FROM ""Refunds"" WHERE ""Status"" = 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestockDecidedAt",
                table: "RefundItems");

            migrationBuilder.DropColumn(
                name: "RestockDecidedByUserId",
                table: "RefundItems");

            migrationBuilder.DropColumn(
                name: "RestockDecision",
                table: "RefundItems");
        }
    }
}
