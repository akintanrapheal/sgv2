using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Text.RegularExpressions;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Services.Marketing;

/// <summary>Renders the drag-and-drop campaign composer's JSON block model into email-safe,
/// inline-styled HTML (tables + bulletproof buttons) for <c>Campaign.BodyHtml</c>. The output is
/// server-trusted: every piece of user text is HTML-encoded, URLs are scheme-checked, colours and
/// fonts are whitelisted before they reach a style attribute, and the one free-form "html" block is
/// run through the rich sanitiser — so the result is stored directly without the blanket SanitizeRich
/// that would otherwise strip images/tables/buttons.
///
/// The stored JSON is either a bare array of blocks (legacy) or an object
/// <c>{ "globals": {...}, "blocks": [...] }</c> carrying the Global-Styles page defaults; both are
/// accepted so old campaigns round-trip unchanged.</summary>
public static class CampaignLayoutRenderer
{
    public class Col
    {
        public string? Src { get; set; }
        public string? Title { get; set; }
        public string? Caption { get; set; }
        public string? Price { get; set; }
        public string? Href { get; set; }
        public string? BtnText { get; set; }
    }

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

        // ── Styling (all optional; defaults match the original fixed look) ──
        public string? Font { get; set; }
        public int? FontSize { get; set; }
        public int? Weight { get; set; }
        public bool? Italic { get; set; }
        public bool? Underline { get; set; }
        public string? Color { get; set; }
        public string? Bg { get; set; }
        public double? LineHeight { get; set; }
        public int? Letter { get; set; }
        public int? Width { get; set; }
        public int? Radius { get; set; }
        public int? Thickness { get; set; }
        public bool? Full { get; set; }
        public int? PadT { get; set; }
        public int? PadR { get; set; }
        public int? PadB { get; set; }
        public int? PadL { get; set; }
        public string? Vis { get; set; }

        public List<Col>? Cols { get; set; }
    }

    /// <summary>Page-wide defaults from the Global Styles tab. Each is a fallback: a block that sets its
    /// own value always wins. All-null (the default) reproduces the original fixed look byte-for-byte.</summary>
    public class Globals
    {
        public string? PageBg { get; set; }       // email/content background
        public string? Font { get; set; }         // default font family
        public int? TextSize { get; set; }        // default body text size
        public string? TextColor { get; set; }    // default text colour
        public double? LineHeight { get; set; }    // default line height
        public string? LinkColor { get; set; }    // default link/social colour
        public string? BtnBg { get; set; }        // default button background
        public string? BtnColor { get; set; }     // default button text colour
        public int? BtnRadius { get; set; }
        public int? BtnSize { get; set; }
        public int? BtnWeight { get; set; }
        public int? Width { get; set; }           // content max-width (px)

        public bool HasAny =>
            !string.IsNullOrWhiteSpace(PageBg) || !string.IsNullOrWhiteSpace(Font) || TextSize is not null
            || !string.IsNullOrWhiteSpace(TextColor) || LineHeight is not null || !string.IsNullOrWhiteSpace(LinkColor)
            || !string.IsNullOrWhiteSpace(BtnBg) || !string.IsNullOrWhiteSpace(BtnColor)
            || BtnRadius is not null || BtnSize is not null || BtnWeight is not null || Width is not null;
    }

    public class Layout
    {
        public Globals? Globals { get; set; }
        public List<Block> Blocks { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly Dictionary<string, string> Fonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arial"] = "Arial, Helvetica, sans-serif",
        ["helvetica"] = "Helvetica, Arial, sans-serif",
        ["georgia"] = "Georgia, 'Times New Roman', serif",
        ["times"] = "'Times New Roman', Times, serif",
        ["courier"] = "'Courier New', Courier, monospace",
        ["tahoma"] = "Tahoma, Geneva, sans-serif",
        ["verdana"] = "Verdana, Geneva, sans-serif",
        ["trebuchet"] = "'Trebuchet MS', Helvetica, sans-serif",
    };

    /// <summary>Parses just the block list (legacy array or the blocks of the new object form).</summary>
    public static List<Block> Parse(string? layoutJson) => ParseLayout(layoutJson).Blocks;

    /// <summary>Parses the stored JSON, accepting both the bare-array (legacy) and
    /// <c>{globals, blocks}</c> shapes. Returns an empty layout for null/blank/invalid input.</summary>
    public static Layout ParseLayout(string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson)) return new();
        var s = layoutJson.TrimStart();
        try
        {
            if (s.StartsWith("["))
                return new Layout { Blocks = JsonSerializer.Deserialize<List<Block>>(layoutJson, JsonOpts) ?? new() };
            return JsonSerializer.Deserialize<Layout>(layoutJson, JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Renders the block model to email HTML. Returns "" when there are no blocks.</summary>
    public static string Render(string? layoutJson)
    {
        var layout = ParseLayout(layoutJson);
        var blocks = layout.Blocks;
        if (blocks.Count == 0) return "";
        var g = layout.Globals ?? new Globals();
        var sb = new StringBuilder();

        // Responsive prelude only when a block actually hides on one device, so the common case stays
        // byte-clean (and the "renders nothing" tests keep seeing "").
        if (blocks.Any(b => Visibility(b.Vis) != "all"))
            sb.Append("<style>@media only screen and (max-width:480px){" +
                      ".sl-hide-mobile{display:none!important}" +
                      ".sl-hide-desktop{display:block!important;max-height:none!important;overflow:visible!important}}</style>");

        // Global Styles wrapper: only when something is set, so legacy output is unchanged.
        var (open, close) = Wrapper(g);
        sb.Append(open);
        foreach (var b in blocks) sb.Append(Wrap(b, RenderBlock(b, g)));
        sb.Append(close);
        return sb.ToString();
    }

    private static (string open, string close) Wrapper(Globals g)
    {
        if (!g.HasAny) return ("", "");
        var style = new StringBuilder();
        if (IsHex(g.PageBg)) style.Append($"background:{g.PageBg!.Trim()};");
        if (!string.IsNullOrWhiteSpace(g.Font)) style.Append($"font-family:{Font(g.Font)};");
        if (IsHex(g.TextColor)) style.Append($"color:{g.TextColor!.Trim()};");
        if (g.Width is { } w && w is >= 320 and <= 800) style.Append($"max-width:{w}px;margin:0 auto;");
        if (IsHex(g.PageBg)) style.Append("padding:4px 2px;");
        return style.Length == 0 ? ("", "") : ($"<div style=\"{style}\">", "</div>");
    }

    private static string RenderBlock(Block b, Globals g) => (b.Type ?? "").ToLowerInvariant() switch
    {
        "heading" => Heading(b, g),
        "text" => Text(b, g),
        "image" => Image(b, g),
        "button" => Button(b, g),
        "divider" => Divider(b),
        "spacer" => Spacer(b),
        "social" => Social(b, g),
        "columns" => Columns(b, g),
        "html" => Html(b),
        _ => ""
    };

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Align(string? a) => a switch { "left" => "left", "right" => "right", _ => "center" };

    private static string Visibility(string? v) => (v ?? "all").ToLowerInvariant() switch
    {
        "desktop" => "desktop", "mobile" => "mobile", _ => "all"
    };

    private static string Wrap(Block b, string inner)
    {
        if (string.IsNullOrEmpty(inner)) return inner;
        return Visibility(b.Vis) switch
        {
            "desktop" => $"<div class=\"sl-hide-mobile\">{inner}</div>",
            "mobile" => $"<div class=\"sl-hide-desktop\" style=\"display:none;max-height:0;overflow:hidden\">{inner}</div>",
            _ => inner
        };
    }

    private static bool IsHex(string? c) =>
        !string.IsNullOrWhiteSpace(c) && Regex.IsMatch(c.Trim(), "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");

    private static string Hex(string? c, string fallback) => IsHex(c) ? c!.Trim() : fallback;

    /// <summary>A global hex value when valid, else the built-in fallback — used as the default a block
    /// overrides.</summary>
    private static string GHex(string? global, string fallback) => IsHex(global) ? global!.Trim() : fallback;

    private static string Font(string? f)
    {
        if (string.IsNullOrWhiteSpace(f)) return "Arial, Helvetica, sans-serif";
        var key = f.Split(',')[0].Trim().Replace("'", "").Split(' ')[0];
        return Fonts.TryGetValue(key, out var stack) ? stack : "Arial, Helvetica, sans-serif";
    }

    private static int Clamp(int? v, int def, int min, int max) => Math.Clamp(v ?? def, min, max);

    private static string Pad(Block b)
    {
        if (b.PadT is null && b.PadR is null && b.PadB is null && b.PadL is null) return "";
        int t = Clamp(b.PadT, 0, 0, 120), r = Clamp(b.PadR, 0, 0, 120),
            bo = Clamp(b.PadB, 0, 0, 120), l = Clamp(b.PadL, 0, 0, 120);
        return $"padding:{t}px {r}px {bo}px {l}px;";
    }

    private static string BgStyle(Block b) => IsHex(b.Bg) ? $"background:{b.Bg!.Trim()};" : "";

    private static string SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "#";
        url = url.Trim();
        return (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
             || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            ? url : "#";
    }

    private static string TextStyle(Block b, Globals g, int defSize, int defWeight, string defColor)
    {
        var sb = new StringBuilder();
        sb.Append($"font-family:{Font(b.Font ?? g.Font)};");
        sb.Append($"font-size:{Clamp(b.FontSize, defSize, 9, 72)}px;");
        var lh = b.LineHeight ?? g.LineHeight;
        sb.Append($"line-height:{(lh is { } v && v is >= 0.8 and <= 3 ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : (defSize >= 20 ? "1.3" : "1.6"))};");
        sb.Append($"font-weight:{Clamp(b.Weight, defWeight, 300, 800)};");
        if (b.Italic == true) sb.Append("font-style:italic;");
        if (b.Underline == true) sb.Append("text-decoration:underline;");
        if (b.Letter is { } ls && ls != 0) sb.Append($"letter-spacing:{Math.Clamp(ls, -2, 12)}px;");
        sb.Append($"color:{Hex(b.Color, GHex(g.TextColor, defColor))};");
        return sb.ToString();
    }

    private static string Heading(Block b, Globals g) =>
        $"<div style=\"{TextStyle(b, g, 24, 600, "#111111")}text-align:{Align(b.Align)};{Pad(b)}{BgStyle(b)}margin:0 0 14px\">{Enc(b.Text)}</div>";

    private static string Text(Block b, Globals g)
    {
        var inner = !string.IsNullOrWhiteSpace(b.Html)
            ? ProductHtml.SanitizeRich(b.Html)
            : Enc(b.Text).Replace("\r\n", "\n").Replace("\n", "<br />");
        var ds = g.TextSize is { } t && t is >= 9 and <= 72 ? t : 15;
        return $"<div style=\"{TextStyle(b, g, ds, 400, "#333333")}text-align:{Align(b.Align)};{Pad(b)}{BgStyle(b)}margin:0 0 14px\">{inner}</div>";
    }

    private static string Image(Block b, Globals g)
    {
        if (string.IsNullOrWhiteSpace(b.Src)) return "";
        var w = b.Width is { } ww && ww is >= 40 and <= 600 ? $"width:{ww}px;max-width:100%;" : "max-width:100%;";
        var radius = b.Radius is { } r && r > 0 ? $"border-radius:{Math.Clamp(r, 0, 60)}px;" : "";
        var img = $"<img src=\"{Enc(SafeUrl(b.Src))}\" alt=\"{Enc(b.Alt)}\" " +
                  $"style=\"{w}height:auto;display:inline-block;border:0;{radius}\" />";
        if (!string.IsNullOrWhiteSpace(b.Href))
            img = $"<a href=\"{Enc(SafeUrl(b.Href))}\" target=\"_blank\" rel=\"noopener\">{img}</a>";
        return $"<div style=\"text-align:{Align(b.Align)};{Pad(b)}{BgStyle(b)}margin:0 0 16px\">{img}</div>";
    }

    private static string Button(Block b, Globals g)
    {
        var label = string.IsNullOrWhiteSpace(b.Text) ? "Shop now" : b.Text;
        var bg = Hex(b.Bg, GHex(g.BtnBg, "#c90278"));
        var fg = Hex(b.Color, GHex(g.BtnColor, "#ffffff"));
        var radius = Clamp(b.Radius, g.BtnRadius ?? 6, 0, 40);
        var fs = Clamp(b.FontSize, g.BtnSize ?? 15, 10, 28);
        var wt = Clamp(b.Weight, g.BtnWeight ?? 600, 400, 800);
        var full = b.Full == true;
        var cellStyle = $"background:{bg};border-radius:{radius}px" + (full ? ";width:100%" : "");
        var linkWidth = full ? "display:block;text-align:center;" : "display:inline-block;";
        return $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" " +
               $"style=\"margin:4px auto 18px{(full ? ";width:100%" : "")}\" align=\"{Align(b.Align)}\"><tr>" +
               $"<td style=\"{cellStyle}\">" +
               $"<a href=\"{Enc(SafeUrl(b.Href))}\" target=\"_blank\" rel=\"noopener\" " +
               $"style=\"{linkWidth}padding:12px 26px;font-family:{Font(b.Font ?? g.Font)};font-size:{fs}px;font-weight:{wt};" +
               $"color:{fg};text-decoration:none;border-radius:{radius}px\">{Enc(label)}</a>" +
               "</td></tr></table>";
    }

    private static string Divider(Block b)
    {
        var color = Hex(b.Color, "#e5e7eb");
        var th = Clamp(b.Thickness, 1, 1, 10);
        return $"<hr style=\"border:none;border-top:{th}px solid {color};margin:22px 0\" />";
    }

    private static string Spacer(Block b)
    {
        var h = Math.Clamp(b.Size ?? 24, 4, 160);
        return $"<div style=\"height:{h}px;line-height:{h}px;font-size:0\">&nbsp;</div>";
    }

    private static string Social(Block b, Globals g)
    {
        var color = Hex(b.Color, GHex(g.LinkColor, "#c90278"));
        var links = new List<string>();
        void Add(string? url, string label)
        {
            if (!string.IsNullOrWhiteSpace(url))
                links.Add($"<a href=\"{Enc(SafeUrl(url))}\" target=\"_blank\" rel=\"noopener\" " +
                          $"style=\"color:{color};text-decoration:none;font-weight:600;margin:0 8px\">{label}</a>");
        }
        Add(b.Instagram, "Instagram");
        Add(b.Facebook, "Facebook");
        Add(b.Tiktok, "TikTok");
        if (links.Count == 0) return "";
        return $"<div style=\"text-align:center;margin:8px 0 18px;font-size:14px\">{string.Join("", links)}</div>";
    }

    private static string Columns(Block b, Globals g)
    {
        var cols = (b.Cols ?? new List<Col>()).Where(c => c != null).Take(3).ToList();
        if (cols.Count == 0) return "";
        var bg = Hex(b.Color, GHex(g.BtnBg, "#c90278"));   // button colour; row bg stays clear unless Bg set
        var cellW = cols.Count > 0 ? 100 / cols.Count : 100;
        var cells = new StringBuilder();
        foreach (var c in cols)
        {
            var inner = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(c.Src))
                inner.Append($"<img src=\"{Enc(SafeUrl(c.Src))}\" alt=\"{Enc(c.Title)}\" style=\"width:100%;max-width:100%;height:auto;display:block;border:0;border-radius:4px\" />");
            if (!string.IsNullOrWhiteSpace(c.Title))
                inner.Append($"<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;font-weight:600;color:#111111;margin:10px 0 2px\">{Enc(c.Title)}</div>");
            if (!string.IsNullOrWhiteSpace(c.Caption))
                inner.Append($"<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:13px;color:#555555;margin:0 0 2px\">{Enc(c.Caption)}</div>");
            if (!string.IsNullOrWhiteSpace(c.Price))
                inner.Append($"<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;font-weight:700;color:#111111;margin:2px 0 8px\">{Enc(c.Price)}</div>");
            if (!string.IsNullOrWhiteSpace(c.BtnText))
                inner.Append($"<a href=\"{Enc(SafeUrl(c.Href))}\" target=\"_blank\" rel=\"noopener\" " +
                             $"style=\"display:inline-block;padding:8px 18px;background:{bg};color:#ffffff;" +
                             $"font-family:Arial,Helvetica,sans-serif;font-size:13px;font-weight:600;text-decoration:none;border-radius:6px;margin-top:4px\">{Enc(c.BtnText)}</a>");
            cells.Append($"<td valign=\"top\" width=\"{cellW}%\" style=\"padding:6px;text-align:center;vertical-align:top\">{inner}</td>");
        }
        return $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"100%\" " +
               $"style=\"margin:0 0 16px;{Pad(b)}{BgStyle(b)}\"><tr>{cells}</tr></table>";
    }

    private static string Html(Block b) =>
        string.IsNullOrWhiteSpace(b.Html) ? "" : ProductHtml.SanitizeRich(b.Html);
}
