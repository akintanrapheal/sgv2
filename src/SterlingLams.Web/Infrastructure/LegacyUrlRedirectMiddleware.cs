namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// 301-redirects the old WooCommerce/WordPress URLs (still in Google's index and hit by bots) to the new
/// storefront structure, so ranking/link-equity is preserved and old links stop dead-ending in 404s.
/// The exact page/post list was taken from the old WordPress export; families (products, categories, tags,
/// store-location pages) are matched by pattern. Query strings from old faceted URLs are dropped.
/// </summary>
public sealed class LegacyUrlRedirectMiddleware
{
    private readonly RequestDelegate _next;
    public LegacyUrlRedirectMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext ctx)
    {
        // HEAD as well as GET: crawlers, link checkers and uptime monitors probe old URLs with HEAD, and
        // skipping them here meant those tools saw a 404 for a URL that redirects perfectly well on GET.
        if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
        {
            string? target = null;
            try { target = MapLegacy(ctx.Request.Path.Value ?? ""); }
            catch { target = null; }  // never let a redirect-mapping error break the request
            if (!string.IsNullOrEmpty(target))
            {
                try
                {
                    ctx.Response.Redirect(target, permanent: true); // 301
                    return;
                }
                catch
                {
                    // A malformed Location (e.g. an un-escaped non-ASCII char) must not 500 the request —
                    // fall through to normal handling (which 404s) instead.
                    ctx.Response.Clear();
                }
            }
        }
        await _next(ctx);
    }

    // Exact old path (lower-case, no trailing slash) → new path. Covers every published page + post from
    // the old WordPress site. Checked BEFORE the family patterns below.
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
    {
        // Content / policy pages
        ["/about-sterlin-glams"] = "/Home/About", ["/about-us"] = "/Home/About", ["/about"] = "/Home/About", ["/our-story"] = "/Home/About",
        ["/contact-us"] = "/Home/Contact", ["/contact"] = "/Home/Contact",
        ["/terms-and-conditions"] = "/Home/Terms", ["/terms-of-service"] = "/Home/Terms", ["/terms"] = "/Home/Terms",
        ["/the-privacy-policy"] = "/Home/Privacy", ["/privacy-policy"] = "/Home/Privacy", ["/privacy"] = "/Home/Privacy", ["/cookie-policy"] = "/Home/Privacy",
        ["/payments-and-returns-policy"] = "/Home/Terms",
        ["/my-account"] = "/Account/Login",
        ["/jewelry-delivery-service"] = "/Home/Terms",
        ["/jewelry-faqs"] = "/Home/Contact",
        // Order tracking
        ["/track-jewelry-order"] = "/track", ["/track-your-order"] = "/track",
        // Blog
        ["/blog"] = "/Journal", ["/jewelry-care-guide"] = "/Journal",
        // Shop landing pages → the product list
        ["/jewelry-categories"] = "/products", ["/jewelry-sale"] = "/products", ["/latest-jewelry-products"] = "/products",
        ["/featured-jewelry-collection"] = "/products", ["/top-rated-jewelry"] = "/products", ["/product-directory"] = "/products",
        ["/shop-by-jewelry-category"] = "/products", ["/compare-jewelry-products"] = "/products", ["/place-jewelry-order"] = "/products",
        ["/sg-chic-collection"] = "/products", ["/sg-chic-jewelry-shop"] = "/products", ["/sg-kingsmen"] = "/products",
        ["/sg-kingsmen-shop"] = "/products", ["/sg-packaging"] = "/products", ["/sg-packaging-store"] = "/products",
        // Misc leftover pages → home
        ["/home-glasses"] = "/", ["/html-sitemap"] = "/", ["/page-not-found"] = "/",
        // Old sitemap locations. Yoast served /sitemap_index.xml and WordPress core /wp-sitemap.xml;
        // both are still what Search Console has on file for this site, and both were 404ing, which
        // reads as "sitemap gone" rather than "sitemap moved".
        ["/sitemap_index.xml"] = "/sitemap.xml", ["/wp-sitemap.xml"] = "/sitemap.xml",
        ["/product-sitemap.xml"] = "/sitemap.xml", ["/page-sitemap.xml"] = "/sitemap.xml",
        ["/post-sitemap.xml"] = "/sitemap.xml", ["/product_cat-sitemap.xml"] = "/sitemap.xml",
        // Instagram "link in bio" / quick-links landing → home (brand landing). Can become its own page later.
        ["/quicklinks"] = "/", ["/quick-links"] = "/", ["/links"] = "/", ["/link-in-bio"] = "/", ["/linkinbio"] = "/",
        // Blog posts → the Journal (articles not individually migrated)
        ["/a-jewelry-shop-in-lagos-for-every-occasion-how-to-shop-smart-at-sterlin-glams"] = "/Journal",
        ["/helvetica-austin-bespoke"] = "/Journal",
        ["/how-to-find-the-right-jewelry-shop-in-lagos"] = "/Journal",
        ["/how-to-layer-necklaces-lagos-stylist-guide"] = "/Journal",
        ["/igbo-bridal-jewelry-guide"] = "/Journal",
        ["/ikota-lekki-phase-2-jewelry-guide"] = "/Journal",
        ["/inside-lagos-most-loved-jewelry-store-in-ikeja-ikota"] = "/Journal",
        ["/jewelry-lagos-women-trending-pieces"] = "/Journal",
        ["/jewelry-store-ikeja-shopping-guide"] = "/Journal",
        ["/jewelry-stores-in-abuja-2026-guide"] = "/Journal",
        ["/jewelry-stores-in-lagos-nigeria-insiders-guide"] = "/Journal",
        ["/jewelry-wholesalers-in-lagos-vs-retail-brands-whats-actually-better-for-you"] = "/Journal",
        ["/lagos-bride-jewelry-guide"] = "/Journal",
        ["/nigerian-bridal-jewelry-guide"] = "/Journal",
        ["/nothing-hunts-us-like-the-things-we-didnt-buy"] = "/Journal",
        ["/office-jewelry-lagos-working-woman"] = "/Journal",
        ["/online-jewelry-stores-in-lagos-buyers-guide"] = "/Journal",
        ["/ring-stores-in-lagos-statement-stackable-cocktail"] = "/Journal",
        ["/the-best-jewelry-stores-in-lagos-nigeria-and-what-makes-sterlin-glams-different"] = "/Journal",
        ["/where-to-buy-jewelry-in-nigeria"] = "/Journal",
        ["/why-lagos-women-notice-your-jewelry-first"] = "/Journal",
        ["/yoruba-bridal-jewelry-guide"] = "/Journal",
    };

    /// <summary>New path for a known old URL, or null if it isn't one we remap. (/cart, /checkout,
    /// /wishlist already resolve on the new site, so they're intentionally not remapped — that would loop.)</summary>
    public static string? MapLegacy(string path)
    {
        var p = (path ?? "").TrimEnd('/');
        if (p.Length == 0) return null;
        var lower = p.ToLowerInvariant();

        // 1) Exact page/post matches first (so e.g. a blog post that starts with "jewelry-store" wins over
        //    the store-location pattern below).
        if (Exact.TryGetValue(lower, out var mapped)) return mapped;

        // 2) WordPress families → the closest new page.
        if (lower.StartsWith("/jewelry-store") || lower.StartsWith("/jewellery-store")
            || lower.StartsWith("/jewelry-shop") || lower.StartsWith("/jewellery-shop"))
            return "/Stores";
        if (lower.StartsWith("/tag/") || lower.StartsWith("/category/")) return "/products";
        if (lower.StartsWith("/author/")) return "/";

        // 3) Catalogue (slug-preserving where possible).
        if (lower == "/shop") return "/products";
        if (lower.StartsWith("/shop/")) return Product(p["/shop/".Length..]);
        if (lower.StartsWith("/product/")) return Product(p["/product/".Length..]);
        if (lower.StartsWith("/product-category/"))
        {
            var segs = p["/product-category/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segs.Length > 0 ? $"/products?category={Uri.EscapeDataString(segs[^1])}" : "/products";
        }

        // 4) WordPress system paths.
        if (lower is "/wp-login.php" or "/wp-admin" or "/feed" || lower.StartsWith("/wp-admin/") || lower.StartsWith("/feed/"))
            return "/";

        return null;
    }

    /// <summary>
    /// Old single-product URL (/shop/{slug} or /product/{slug}) → its page on the new storefront.
    /// Retired slugs resolve through <see cref="LegacyProductSlugMap"/> here rather than being bounced to
    /// /products/{slug} first, so the old URL reaches its replacement in one 301 instead of two.
    /// </summary>
    private static string Product(string slug)
    {
        if (slug.Length == 0 || slug.Contains('/')) return "/products";
        if (LegacyProductSlugMap.TryResolve(slug, out var moved)) return moved;
        // Encode the slug: old product URLs can contain non-ASCII (e.g. ₦) which must be %-escaped in
        // the Location header, or setting it throws "Invalid non-ASCII character in header".
        return $"/products/{Uri.EscapeDataString(slug)}";
    }
}
