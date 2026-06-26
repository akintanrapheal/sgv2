namespace SterlingLams.Web.Services;

/// <summary>Builds unique, SEO-friendly rich-text (HTML) product descriptions from a product's name
/// and category — detecting style (stud/hoop/drop, choker/pendant, cuff…), finish (gold/silver/rose/
/// mixed) and adornment (pearl/stone) — plus a shared Sterlin Glams jewellery-care section. Output is
/// sanitised by ProductHtml before saving.</summary>
public class SeoDescriptionGenerator
{
    private const string Care =
        "<h3>Jewellery Care Instructions</h3>"
      + "<p>Your Sterlin Glams piece is crafted from high-grade, gold/platinum-plated brass set with "
      + "semi-precious cubic zirconia stones &mdash; beautifully made costume jewellery designed to be treasured.</p>"
      + "<ul>"
      + "<li>Like all fine costume jewellery, <strong>keep away from harsh liquids</strong> such as perfume, lotion, hand sanitiser and water.</li>"
      + "<li>Always <strong>wipe away body residue</strong> with a soft, dry cloth after each wear.</li>"
      + "<li><strong>Store in a cool, dry place</strong> &mdash; ideally a pouch or jewellery box, out of direct sunlight.</li>"
      + "<li><strong>Nickel &amp; lead free</strong>, so it is gentle on skin and should not cause reactions.</li>"
      + "<li>These are <strong>occasional-wear</strong> pieces (not everyday wear). Cared for properly, they will stay beautiful for several years.</li>"
      + "</ul>";

    private static readonly string[] Intros =
    {
        "Turn heads with the <strong>{0}</strong> from Sterlin Glams &mdash; {1} {2} handcrafted to add instant glamour to any look.",
        "Meet the <strong>{0}</strong> by Sterlin Glams: {1} {2} designed to elevate everything from workwear to evening outfits.",
        "Effortlessly chic, the <strong>{0}</strong> is {1} {2} that brings a polished, put-together finish to your style.",
        "Make a statement with the <strong>{0}</strong> from Sterlin Glams &mdash; beautifully crafted {1} {2} that catches the light with every move.",
        "The <strong>{0}</strong> is {1} {2} from Sterlin Glams, blending modern design with handmade quality for a standout finish.",
        "Add a touch of luxe to your jewellery box with the <strong>{0}</strong> &mdash; {1} {2} made to be noticed.",
    };

    public string Build(int seed, string name, string category)
    {
        var n = (name ?? "").ToLowerInvariant();
        var c = (category ?? "").ToLowerInvariant();
        var safeName = System.Net.WebUtility.HtmlEncode(name ?? "");

        var (noun, style, closeLabel, closeVal) = Categorize(n, c);
        var fin = Finish(n);
        var pearl = n.Contains("pearl");
        var stone = ContainsAny(n, "stone", "crystal", "sparkle", "shiny", "gloss", "zirconia", "laser",
            "shimmer", "bloom", "royal", "glow", "diamond", "ice", "star");

        var typPhrase = string.IsNullOrEmpty(style) ? noun : $"{style} {noun}";

        var intro = string.Format(Intros[Math.Abs(seed) % Intros.Length], safeName, fin, typPhrase);
        intro += pearl
            ? " Finished with elegant faux pearls for a timeless, feminine touch."
            : stone
                ? " Set with brilliant AAA cubic zirconia stones that sparkle from every angle."
                : " A versatile piece that pairs beautifully with any outfit.";

        var bullets = new List<string>
        {
            string.IsNullOrEmpty(style)
                ? $"Beautifully crafted <strong>{noun}</strong> design"
                : $"Eye-catching <strong>{style} {noun}</strong> design"
        };
        if (pearl) bullets.Add("Elegant <strong>faux pearls</strong> for timeless sophistication");
        else if (stone) bullets.Add("Brilliant <strong>cubic zirconia</strong> stones for all-out sparkle");
        bullets.Add($"Beautiful {fin} finish with long-lasting plating");
        bullets.Add("Lightweight and comfortable for all-day wear");
        bullets.Add("<strong>Nickel &amp; lead free</strong> &mdash; gentle on sensitive skin");
        bullets.Add("Perfect for weddings, parties, gifting and special occasions");

        var details = new List<string> { $"<li><strong>Material:</strong> brass base with premium {fin} plating</li>" };
        if (pearl) details.Add("<li><strong>Stones:</strong> elegant faux pearls</li>");
        else if (stone) details.Add("<li><strong>Stones:</strong> AAA cubic zirconia</li>");
        if (!string.IsNullOrEmpty(closeLabel)) details.Add($"<li><strong>{closeLabel}:</strong> {closeVal}</li>");
        details.Add($"<li><strong>Style:</strong> {typPhrase}</li>");

        return $"<h3>{safeName}</h3><p>{intro}</p>"
             + "<h3>Why you will love it</h3><ul>" + string.Concat(bullets.ConvertAll(b => $"<li>{b}</li>")) + "</ul>"
             + "<h3>Product details</h3><ul>" + string.Concat(details) + "</ul>"
             + Care;
    }

    private static (string noun, string style, string closeLabel, string closeVal) Categorize(string n, string c)
    {
        string Pick(params (string kw, string val)[] m)
        {
            foreach (var (kw, val) in m) if (n.Contains(kw)) return val;
            return "";
        }

        if (c.Contains("earring"))
        {
            var st = Pick(("stud", "stud"), ("hoop", "hoop"), ("loop", "hoop"), ("orbit", "hoop"), ("spiral", "hoop"),
                ("curl", "hoop"), ("coil", "hoop"), ("huggie", "hoop"), ("drop", "drop"), ("drape", "drop"),
                ("teardrop", "drop"), ("tassel", "drop"), ("dangle", "drop"), ("chandelier", "drop"));
            var close = st == "stud" ? "secure stud post with butterfly backs"
                : st == "drop" ? "easy hook fastening"
                : st == "hoop" ? "comfortable hinged fastening" : "secure fastening";
            return ("earrings", st, "Fastening", close);
        }
        if (c.Contains("necklace"))
            return ("necklace", Pick(("choker", "choker"), ("pendant", "pendant"), ("layered", "layered"),
                ("layer", "layered"), ("chain", "chain")), "Chain", "adjustable chain with a secure lobster clasp");
        if (c.Contains("anklet"))
            return ("anklet", Pick(("layered", "layered"), ("layer", "layered"), ("ball", "beaded"),
                ("bead", "beaded"), ("stone", "stone")), "Chain", "adjustable chain with a secure clasp");
        if (c.Contains("ring"))
            return ("ring", Pick(("band", "band"), ("stack", "stacking"), ("statement", "statement")), "Fit", "comfortable, true-to-size band");
        if (c.Contains("bangle") || c.Contains("bracelet"))
        {
            var noun = n.Contains("bangle") ? "bangle" : n.Contains("bracelet") ? "bracelet" : (c.Contains("bangle") ? "bangle" : "bracelet");
            var st = Pick(("cuff", "cuff"), ("tennis", "tennis"), ("beaded", "beaded"), ("bead", "beaded"),
                ("chain", "chain"), ("charm", "charm"));
            return (noun, st, "Fit", "comfortable, easy-to-wear fit");
        }
        if (c.Contains("set")) return ("jewellery set", "coordinated", "", "");
        return ("piece", "", "", "");
    }

    private static string Finish(string n)
    {
        if (n.Contains("rose")) return "rose-gold";
        if (n.Contains("silver") || n.Contains("white")) return "silver-tone";
        if (n.Contains("black")) return "sleek black";
        if (ContainsAny(n, "2tone", "3tone", "2 tone", "3 tone", "tone", "mix", "multi", "colour", "color")) return "mixed-metal";
        return "gold-tone";
    }

    private static bool ContainsAny(string n, params string[] kws)
    {
        foreach (var k in kws) if (n.Contains(k)) return true;
        return false;
    }
}
