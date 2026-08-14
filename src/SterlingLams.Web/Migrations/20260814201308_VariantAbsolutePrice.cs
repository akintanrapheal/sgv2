using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class VariantAbsolutePrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column used to hold a price *delta* (base + adjustment). It now holds the variant's
            // absolute selling price. Rename, then convert the stored deltas into absolute prices so
            // every variant keeps the exact price it was selling at.
            migrationBuilder.RenameColumn(
                name: "PriceAdjustment",
                table: "ProductVariants",
                newName: "Price");

            // A null adjustment meant "follow the base price" → keep it null (still follows base).
            // A set adjustment (including 0) becomes an explicit price = base + old delta.
            migrationBuilder.Sql(@"
                UPDATE ""ProductVariants"" v
                SET ""Price"" = p.""Price"" + v.""Price""
                FROM ""Products"" p
                WHERE p.""Id"" = v.""ProductId"" AND v.""Price"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the conversion (absolute price → delta) before renaming back.
            migrationBuilder.Sql(@"
                UPDATE ""ProductVariants"" v
                SET ""Price"" = v.""Price"" - p.""Price""
                FROM ""Products"" p
                WHERE p.""Id"" = v.""ProductId"" AND v.""Price"" IS NOT NULL;");

            migrationBuilder.RenameColumn(
                name: "Price",
                table: "ProductVariants",
                newName: "PriceAdjustment");
        }
    }
}
