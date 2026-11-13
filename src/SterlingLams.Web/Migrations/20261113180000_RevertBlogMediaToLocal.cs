using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class RevertBlogMediaToLocal : Migration
    {
        // DATA-ONLY (no schema change — snapshot unchanged). Blog media is served from wwwroot/media/blog,
        // NOT R2: the storefront serves R2 images through pre-sized …_<w>.webp variants and those 404 for
        // the small recovered blog images, so point the two posts back at the local originals (which render
        // directly). Idempotent — once the URLs are local there's nothing left to replace.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
UPDATE "BlogPosts"
   SET "Body" = REPLACE("Body", 'https://img.sterlinglams.com/blog/', '/media/blog/'),
       "CoverImageUrl" = REPLACE("CoverImageUrl", 'https://img.sterlinglams.com/blog/', '/media/blog/'),
       "UpdatedAt" = now()
 WHERE "Body" LIKE '%img.sterlinglams.com/blog/%'
    OR "CoverImageUrl" LIKE '%img.sterlinglams.com/blog/%';
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible (we don't re-point media back at R2). Intentionally a no-op.
        }
    }
}
