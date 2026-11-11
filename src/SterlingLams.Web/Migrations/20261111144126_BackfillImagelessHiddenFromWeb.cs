using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class BackfillImagelessHiddenFromWeb : Migration
    {
        // DATA-ONLY migration — the HiddenFromWeb column itself was added by the earlier
        // 20261110150121_AddProductHiddenFromWeb migration, so there is no schema change here.
        // One-time backfill: every product that is currently shown on the website (HiddenFromWeb = false)
        // but has NO photo is taken off the storefront and sold at the till only. IsActive is left alone,
        // so these items stay active and sellable at the till; they return to the website once a picture is
        // added and "Available online" is ticked again. Products that already have an image are untouched.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""Products"" AS p
                   SET ""HiddenFromWeb"" = TRUE
                 WHERE p.""HiddenFromWeb"" = FALSE
                   AND NOT EXISTS (
                       SELECT 1 FROM ""ProductImages"" AS i WHERE i.""ProductId"" = p.""Id"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: we can't tell which products were flipped by this backfill versus hidden by
            // staff for other reasons, so undoing it could wrongly re-expose items. Intentionally a no-op.
        }
    }
}
