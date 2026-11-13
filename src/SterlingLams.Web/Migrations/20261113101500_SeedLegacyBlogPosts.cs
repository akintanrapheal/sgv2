using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SterlingLams.Web.Migrations
{
    /// <inheritdoc />
    public partial class SeedLegacyBlogPosts : Migration
    {
        // DATA-ONLY migration (no schema change — the model snapshot is unchanged).
        // Re-publishes two original posts from the old WordPress site onto the new Blog, back-dated to
        // their real date (14 Nov 2019). Media was recovered from the Wayback Machine and is served from
        // wwwroot/media/blog. Idempotent: each row is inserted only if its slug isn't already present, so
        // a re-run (or an owner who hand-created the slug first) can't duplicate or fail the deploy.
        // The raw-string closing delimiter is flush-left so the embedded HTML can start at any column.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dollar-quoted ($sg$...$sg$) string literals so the HTML bodies need no escaping.
            migrationBuilder.Sql(""""
INSERT INTO "BlogPosts"
    ("Title","Slug","Excerpt","Body","CoverImageUrl","AuthorName","IsPublished","PublishedAt","MetaTitle","MetaDescription","CreatedAt","UpdatedAt")
SELECT
    $sg$Rock me baby!!$sg$,
    $sg$rock-me-baby$sg$,
    $sg$Business or pleasure — master formal dotting in one of our sharp collections, now available in so many colours.$sg$,
    $sg$<p>For your weekend — business or pleasure, master formal dotting in one of our sharp collections. Our bestselling luxury set is now available in so many different colours.</p>
<div class="clip-grid"><video controls muted loop playsinline preload="metadata"><source src="/media/blog/rock-me-baby-1.mp4" type="video/mp4"></video><video controls muted loop playsinline preload="metadata"><source src="/media/blog/rock-me-baby-2.mp4" type="video/mp4"></video><video controls muted loop playsinline preload="metadata"><source src="/media/blog/rock-me-baby-3.mp4" type="video/mp4"></video><video controls muted loop playsinline preload="metadata"><source src="/media/blog/rock-me-baby-4.mp4" type="video/mp4"></video></div>
<p>Refresh your weekend outing, thanks to this piece.</p>$sg$,
    $sg$/media/blog/rock-me-baby-cover.jpg$sg$,
    $sg$Sterlin Glams$sg$,
    TRUE,
    TIMESTAMPTZ '2019-11-14 08:00:00+00',
    NULL,
    $sg$Business or pleasure — master formal dotting in one of our sharp collections, now in so many colours.$sg$,
    TIMESTAMPTZ '2019-11-14 08:00:00+00',
    TIMESTAMPTZ '2019-11-14 08:00:00+00'
WHERE NOT EXISTS (SELECT 1 FROM "BlogPosts" WHERE "Slug" = $sg$rock-me-baby$sg$);

INSERT INTO "BlogPosts"
    ("Title","Slug","Excerpt","Body","CoverImageUrl","AuthorName","IsPublished","PublishedAt","MetaTitle","MetaDescription","CreatedAt","UpdatedAt")
SELECT
    $sg$Nothing hunts us like the things we didn't buy!$sg$,
    $sg$nothing-hunts-us-like-the-things-we-didnt-buy$sg$,
    $sg$Jewellery and watches are the finishing touches no outfit can do without — explore our handmade statement pieces.$sg$,
    $sg$<figure><img src="/media/blog/nothing-hunts-banner.jpg" alt="Sterlin Glams handmade statement jewellery"></figure>
<h2>Stunning collections of handmade statement jewellery</h2>
<p>Jewellery and watches — they're the finishing touches no outfit can do without. Our styles, from cuffs to chokers, feature Skin Kind technology, which goes above and beyond usual hypoallergenic standards.</p>
<p><a href="/products">Explore the collection →</a></p>$sg$,
    $sg$/media/blog/nothing-hunts-banner.jpg$sg$,
    $sg$Sterlin Glams$sg$,
    TRUE,
    TIMESTAMPTZ '2019-11-14 09:30:00+00',
    NULL,
    $sg$Jewellery and watches are the finishing touches no outfit can do without — explore Sterlin Glams handmade statement jewellery.$sg$,
    TIMESTAMPTZ '2019-11-14 09:30:00+00',
    TIMESTAMPTZ '2019-11-14 09:30:00+00'
WHERE NOT EXISTS (SELECT 1 FROM "BlogPosts" WHERE "Slug" = $sg$nothing-hunts-us-like-the-things-we-didnt-buy$sg$);
"""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversible: removes exactly the two seeded posts (safe — slugs are unique to this seed).
            migrationBuilder.Sql(""""
DELETE FROM "BlogPosts" WHERE "Slug" IN ($sg$rock-me-baby$sg$, $sg$nothing-hunts-us-like-the-things-we-didnt-buy$sg$);
"""");
        }
    }
}
