using Ganss.Xss;

namespace SterlingLams.Web.Services;

/// <summary>
/// Sanitises the rich-text product description authored by staff in the back office before it is
/// stored and rendered on the storefront. Only a small allow-list of formatting tags / styles is
/// permitted (the same set the toolbar can produce) — everything else (scripts, event handlers,
/// iframes, javascript: URLs, …) is stripped. The configured <see cref="HtmlSanitizer"/> is created
/// once and reused; its Sanitize call is safe to invoke concurrently.
/// </summary>
public static class ProductHtml
{
    private static readonly HtmlSanitizer _sanitizer = CreateSanitizer(allowLinks: false);
    private static readonly HtmlSanitizer _richSanitizer = CreateSanitizer(allowLinks: true);
    private static readonly HtmlSanitizer _articleSanitizer = CreateArticleSanitizer();

    private static HtmlSanitizer CreateSanitizer(bool allowLinks)
    {
        var s = new HtmlSanitizer();

        s.AllowedTags.Clear();
        foreach (var t in new[]
        {
            "p", "br", "div", "span",
            "b", "strong", "i", "em", "u", "s", "strike", "del",
            "ul", "ol", "li",
            "h2", "h3", "h4", "blockquote", "font"
        })
            s.AllowedTags.Add(t);

        s.AllowedAttributes.Clear();
        s.AllowedAttributes.Add("style");
        s.AllowedAttributes.Add("color");   // legacy <font color>
        s.AllowedAttributes.Add("face");    // legacy <font face>
        s.AllowedAttributes.Add("size");    // legacy <font size>

        s.AllowedCssProperties.Clear();
        foreach (var c in new[]
        {
            "text-align", "font-weight", "font-style",
            "text-decoration", "text-decoration-line",
            "font-family", "font-size", "color"
        })
            s.AllowedCssProperties.Add(c);

        // Product descriptions: no links by design. Rich content (marketing emails) may link.
        s.AllowedSchemes.Clear();
        if (allowLinks)
        {
            s.AllowedTags.Add("a");
            s.AllowedAttributes.Add("href");
            s.AllowedAttributes.Add("target");
            s.AllowedAttributes.Add("rel");
            s.AllowedSchemes.Add("http");
            s.AllowedSchemes.Add("https");
            s.AllowedSchemes.Add("mailto");
            // javascript:/data: hrefs are dropped automatically (not in AllowedSchemes).
        }

        return s;
    }

    // Blog articles are long-form, admin-authored content that legitimately needs links, images and
    // video (e.g. re-published legacy posts with embedded clips). Built on the rich (links) set, plus a
    // tight media allow-list. Scripts, event handlers, iframes and javascript:/data: URLs are still
    // stripped, so the storefront XSS surface doesn't widen.
    private static HtmlSanitizer CreateArticleSanitizer()
    {
        var s = CreateSanitizer(allowLinks: true);
        foreach (var t in new[] { "a", "img", "figure", "figcaption", "video", "source" })
            s.AllowedTags.Add(t);
        foreach (var a in new[]
        {
            "href", "target", "rel",                                   // links (also added by allowLinks)
            "src", "alt", "title", "width", "height", "loading",       // images
            "controls", "muted", "loop", "autoplay", "playsinline", "poster", "preload", "type", // video/source
            "class",                                                   // layout helpers (e.g. clip grids) — classes can't execute
        })
            s.AllowedAttributes.Add(a);
        return s;
    }

    /// <summary>Strip everything outside the formatting allow-list (NO links). Returns "" for null/blank.</summary>
    public static string Sanitize(string? html)
        => string.IsNullOrWhiteSpace(html) ? "" : _sanitizer.Sanitize(html);

    /// <summary>Sanitise a blog article body — allows safe links, images and &lt;video&gt;, nothing
    /// executable. Use for Journal/Blog post bodies (authoring + render).</summary>
    public static string SanitizeArticle(string? html)
        => string.IsNullOrWhiteSpace(html) ? "" : _articleSanitizer.Sanitize(html);

    /// <summary>Like <see cref="Sanitize"/> but also permits safe &lt;a href&gt; links — for
    /// marketing email bodies (CTAs / tracked links).</summary>
    public static string SanitizeRich(string? html)
        => string.IsNullOrWhiteSpace(html) ? "" : _richSanitizer.Sanitize(html);

    /// <summary>Heuristic: does this description contain HTML markup (vs. legacy plain text)?</summary>
    public static bool LooksLikeHtml(string? value)
        => !string.IsNullOrEmpty(value) && value.Contains('<');

    /// <summary>Strip all markup down to plain text (for meta tags / JSON-LD / SEO).</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = System.Text.RegularExpressions.Regex.Replace(Sanitize(html), "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }
}
