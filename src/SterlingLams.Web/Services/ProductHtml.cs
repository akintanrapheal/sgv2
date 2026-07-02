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

    /// <summary>Strip everything outside the formatting allow-list (NO links). Returns "" for null/blank.</summary>
    public static string Sanitize(string? html)
        => string.IsNullOrWhiteSpace(html) ? "" : _sanitizer.Sanitize(html);

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
