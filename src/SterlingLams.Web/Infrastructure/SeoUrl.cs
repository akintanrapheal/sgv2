namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// The site's single canonical origin (scheme + host) for SEO. Canonical tags, og:url, JSON-LD and the
/// sitemap all build URLs from this, so every page agrees on ONE address no matter how the visitor reached
/// it — www vs apex, mixed case, a trailing slash, or the Render URL. Without it each variant
/// self-canonicalises and Google files them as "Duplicate without user-selected canonical".
/// Defaults to the apex domain; override once at startup from config (App:CanonicalBaseUrl).
/// </summary>
public static class SeoUrl
{
    private static volatile string _base = "https://sterlinglams.com";

    /// <summary>Scheme + host only, no trailing slash (e.g. https://sterlinglams.com).</summary>
    public static string Base => _base;

    /// <summary>Set the canonical origin from config. Ignored unless it's a valid absolute URL, so a blank
    /// or malformed value safely keeps the apex default.</summary>
    public static void Configure(string? baseUrl)
    {
        var b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length > 0 && Uri.TryCreate(b, UriKind.Absolute, out var u))
            _base = $"{u.Scheme}://{u.Authority}";
    }

    /// <summary>Canonical absolute URL for a request path: the fixed base + a normalised path
    /// (lower-cased, trailing slash stripped, query dropped).</summary>
    public static string Canonical(string? path) => _base + NormalizePath(path);

    /// <summary>Lower-case the path and strip a trailing slash (root stays "/").</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        var p = path.ToLowerInvariant();
        if (p.Length > 1) p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }
}
