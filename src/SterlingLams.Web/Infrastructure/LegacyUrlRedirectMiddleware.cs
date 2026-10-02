namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// 301-redirects old WooCommerce/WordPress URLs (still indexed by Google and hit by bots) to the new
/// storefront structure, so ranking/link-equity is preserved and the old links stop dead-ending in 404s.
///   /shop                         → /products
///   /shop/{slug}                  → /products/{slug}   (old product permalink)
///   /product/{slug}               → /products/{slug}
///   /product-category/…/{cat}     → /products?category={cat}
///   /wp-login.php, /wp-admin, /feed → /   (bot/old-link noise)
/// Query strings from the old faceted URLs (filter_color, per_page, orderby…) are dropped — the new
/// pages have their own facets. Unknown WordPress paths (wp-json, wp-content, xmlrpc) fall through to 404.
/// </summary>
public sealed class LegacyUrlRedirectMiddleware
{
    private readonly RequestDelegate _next;
    public LegacyUrlRedirectMiddleware(RequestDelegate next) => _next = next;

    public async Task Invoke(HttpContext ctx)
    {
        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            var target = MapLegacy(ctx.Request.Path.Value ?? "");
            if (target != null)
            {
                ctx.Response.Redirect(target, permanent: true); // 301
                return;
            }
        }
        await _next(ctx);
    }

    /// <summary>New path (with query) for a known old URL, or null if it isn't one we remap.</summary>
    public static string? MapLegacy(string path)
    {
        var p = (path ?? "").TrimEnd('/');
        if (p.Length == 0) return null;
        var lower = p.ToLowerInvariant();

        if (lower == "/shop") return "/products";
        if (lower.StartsWith("/shop/"))
        {
            var slug = p["/shop/".Length..];
            return slug.Length == 0 || slug.Contains('/') ? "/products" : $"/products/{slug}";
        }
        if (lower.StartsWith("/product/"))
        {
            var slug = p["/product/".Length..];
            return slug.Length == 0 || slug.Contains('/') ? "/products" : $"/products/{slug}";
        }
        if (lower.StartsWith("/product-category/"))
        {
            var segs = p["/product-category/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segs.Length > 0 ? $"/products?category={Uri.EscapeDataString(segs[^1])}" : "/products";
        }
        if (lower is "/wp-login.php" or "/wp-admin" or "/feed" || lower.StartsWith("/wp-admin/") || lower.StartsWith("/feed/"))
            return "/";

        return null;
    }
}
