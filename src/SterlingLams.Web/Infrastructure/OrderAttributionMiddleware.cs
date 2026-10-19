namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Order attribution (WooCommerce-style): the visit's origin (Direct / referring host / utm_source)
/// and a page-view count, stamped onto the order at checkout.
///
/// This used to be a middleware that wrote the session on every storefront GET — but touching the
/// session sets a cookie, and a response that sets a cookie can't be stored by the output cache (or
/// by Cloudflare). That left the high-traffic storefront pages uncacheable, so every hit — including
/// bot floods — ran full DB queries and could exhaust the small Postgres (2026-10-19 outage).
///
/// Attribution is now captured CLIENT-SIDE into plain cookies (see the inline script in _Layout):
/// first-touch <c>sg_oa</c> (origin) + <c>sg_opv</c> (page views). The pages themselves set no cookie,
/// so they stay fully cacheable, and the client script still runs on cache hits. Checkout reads the
/// cookies here. Values are visitor-controllable, so they're treated as untrusted display data
/// (trimmed/length-capped here; the admin view HTML-encodes).
/// </summary>
public static class OrderAttributionMiddleware
{
    /// <summary>First-touch origin (Direct / referrer host / utm_source), set client-side, never overwritten.</summary>
    public const string OriginCookie = "sg_oa";
    /// <summary>Page-view counter for the visit, incremented client-side.</summary>
    public const string PageViewsCookie = "sg_opv";

    /// <summary>Reads the attribution origin from the client cookie; "Direct" when absent. Trimmed and
    /// length-capped since it's visitor-supplied.</summary>
    public static string OriginFrom(HttpRequest req)
    {
        var v = req.Cookies[OriginCookie];
        if (string.IsNullOrWhiteSpace(v)) return "Direct";
        v = v.Trim();
        return v.Length > 100 ? v[..100] : v;
    }

    /// <summary>Reads the client-side page-view count for the visit, or null when absent/invalid.</summary>
    public static int? PageViewsFrom(HttpRequest req)
        => int.TryParse(req.Cookies[PageViewsCookie], out var n) && n > 0 && n < 100000 ? n : null;

    /// <summary>Mobile / Tablet / Desktop from a user-agent string.</summary>
    public static string DeviceFromUserAgent(string? ua)
    {
        ua = (ua ?? "").ToLowerInvariant();
        if (ua.Contains("ipad") || ua.Contains("tablet")) return "Tablet";
        if (ua.Contains("mobi") || ua.Contains("iphone") || ua.Contains("android")) return "Mobile";
        return string.IsNullOrWhiteSpace(ua) ? "Unknown" : "Desktop";
    }
}
