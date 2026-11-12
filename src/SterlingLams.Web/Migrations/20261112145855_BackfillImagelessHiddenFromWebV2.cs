using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class BackfillImagelessHiddenFromWebV2 : Migration
    {
        // DATA-ONLY (no schema change). A second pass of the imageless backfill: the WooCommerce import
        // path wasn't applying the "no photo → website-hidden" rule, so some products slipped onto the
        // storefront with no picture. This re-hides every product that is currently shown on the website
        // (HiddenFromWeb = false) but has NO image with a real URL — including rows that carry only an
        // empty-URL image placeholder. IsActive is left alone (they stay till-sellable); they return to
        // the website once a picture is added and "Available online" is ticked again.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""Products"" AS p
                   SET ""HiddenFromWeb"" = TRUE
                 WHERE p.""HiddenFromWeb"" = FALSE
                   AND NOT EXISTS (
                       SELECT 1 FROM ""ProductImages"" AS i
                        WHERE i.""ProductId"" = p.""Id""
                          AND COALESCE(i.""Url"", '') <> '');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible (can't tell which rows this flipped vs. staff-hidden ones). Intentionally a no-op.
        }
    }
}
