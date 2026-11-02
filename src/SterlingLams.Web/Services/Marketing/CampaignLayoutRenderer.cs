using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Services.Marketing;

/// <summary>Renders the drag-and-drop campaign composer's JSON block model into email-safe,
/// inline-styled HTML (tables + bulletproof buttons) for <c>Campaign.BodyHtml</c>. The output is
/// server-trusted: every piece of user text is HTML-encoded, URLs are scheme-checked, and the one
/// free-form "html" block is run through the rich sanitiser — so the result is stored directly
/// without the blanket SanitizeRich that would otherwise strip images/tables/buttons.</summary>
public static class CampaignLayoutRenderer
{
    public class Block
    {
        public string Type { get; set; } = "";
        public string? Text { get; set; }
        public string? Html { get; set; }
        public string? Src { get; set; }
        public string? Href { get; set; }
        public string? Alt { get; set; }
        public string? Align { get; set; }
        public int? Size { get; set; }
        public string? Instagram { get; set; }
        public string? Facebook { get; set; }
        public string? Tiktok { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>Parses the JSON block model. Returns an empty list for null/blank/invalid input.</summary>
    public static List<Block> Parse(string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson)) return new();
        try { return JsonSerializer.Deserialize<List<Block>>(layoutJson, JsonOpts) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Renders the block model to email HTML. Returns "" when there are no blocks.</summary>
    public static string Render(string? layoutJson)
    {
        var blocks = Parse(layoutJson);
        if (blocks.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var b in blocks) sb.Append(RenderBlock(b));
        return sb.ToString();
    }

    private static string RenderBlock(Block b) => (b.Type ?? "").ToLowerInvariant() switch
    {
        "heading" => Heading(b),
        "text" => Text(b),
        "image" => Image(b),
        "button" => Button(b),
        "divider" => "<hr style=\"border:none;border-top:1px solid #e5e7eb;margin:22px 0\" />",
        "spacer" => Spacer(b),
        "social" => Social(b),
        "html" => Html(b),
        _ => ""
    };

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Align(string? a) => a switch
    {
        "left" => "left", "right" => "right", _ => "center"
    };

    /// <summary>Only http(s)/mailto links are emitted; anything else becomes "#" so a bad or
    /// javascript: URL can never reach the recipient.</summary>
    private static string SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "#";
        url = url.Trim();
        return (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
             || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            ? url : "#";
    }

    private static string Heading(Block b) =>
        $"<div style=\"font-size:24px;line-height:1.3;font-weight:600;color:#111111;" +
        $"text-align:{Align(b.Align)};margin:0 0 14px\">{Enc(b.Text)}</div>";

    private static string Text(Block b)
    {
        // A text block may carry light rich formatting (html); otherwise the plain text keeps its
        // line breaks as <br>.
        var inner = !string.IsNullOrWhiteSpace(b.Html)
            ? ProductHtml.SanitizeRich(b.Html)
            : Enc(b.Text).Replace("\r\n", "\n").Replace("\n", "<br />");
        return $"<div style=\"font-size:15px;line-height:1.6;color:#333333;" +
               $"text-align:{Align(b.Align)};margin:0 0 14px\">{inner}</div>";
    }

    private static string Image(Block b)
    {
        if (string.IsNullOrWhiteSpace(b.Src)) return "";
        var img = $"<img src=\"{Enc(SafeUrl(b.Src))}\" alt=\"{Enc(b.Alt)}\" " +
                  "style=\"max-width:100%;height:auto;display:block;margin:0 auto;border:0\" />";
        if (!string.IsNullOrWhiteSpace(b.Href))
            img = $"<a href=\"{Enc(SafeUrl(b.Href))}\" target=\"_blank\" rel=\"noopener\">{img}</a>";
        return $"<div style=\"text-align:{Align(b.Align)};margin:0 0 16px\">{img}</div>";
    }

    private static string Button(Block b)
    {
        var label = string.IsNullOrWhiteSpace(b.Text) ? "Shop now" : b.Text;
        return $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" " +
               $"style=\"margin:4px auto 18px\" align=\"{Align(b.Align)}\"><tr>" +
               "<td style=\"background:#c90278;border-radius:6px\">" +
               $"<a href=\"{Enc(SafeUrl(b.Href))}\" target=\"_blank\" rel=\"noopener\" " +
               "style=\"display:inline-block;padding:12px 26px;font-size:15px;font-weight:600;" +
               $"color:#ffffff;text-decoration:none;border-radius:6px\">{Enc(label)}</a>" +
               "</td></tr></table>";
    }

    private static string Spacer(Block b)
    {
        var h = Math.Clamp(b.Size ?? 24, 4, 160);
        return $"<div style=\"height:{h}px;line-height:{h}px;font-size:0\">&nbsp;</div>";
    }

    private static string Social(Block b)
    {
        var links = new List<string>();
        void Add(string? url, string label)
        {
            if (!string.IsNullOrWhiteSpace(url))
                links.Add($"<a href=\"{Enc(SafeUrl(url))}\" target=\"_blank\" rel=\"noopener\" " +
                          $"style=\"color:#c90278;text-decoration:none;font-weight:600;margin:0 8px\">{label}</a>");
        }
        Add(b.Instagram, "Instagram");
        Add(b.Facebook, "Facebook");
        Add(b.Tiktok, "TikTok");
        if (links.Count == 0) return "";
        return $"<div style=\"text-align:center;margin:8px 0 18px;font-size:14px\">{string.Join("", links)}</div>";
    }

    private static string Html(Block b) =>
        string.IsNullOrWhiteSpace(b.Html) ? "" : ProductHtml.SanitizeRich(b.Html);
}
