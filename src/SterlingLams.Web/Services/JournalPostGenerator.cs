using System.Text;

namespace SterlingLams.Web.Services;

/// <summary>
/// Composes an elegant, brand-styled Journal (blog) post from a handful of real products.
/// Template-based and deterministic per <c>seed</c> — the same seed always yields the same post — so
/// the admin "Generate" tool can preview a post and then create exactly what was shown. There are no
/// AI calls or API keys; it runs against whatever database the app is connected to, so it works on the
/// live site directly (like the SEO description tool). Output HTML is sanitised by ProductHtml before
/// saving and is fully editable afterwards in the Journal editor.
/// </summary>
public class JournalPostGenerator
{
    /// <summary>One product the post can feature (already resolved to name/price/image/category).</summary>
    public record Featured(string Name, decimal Price, string? ImageUrl, string Category);

    public record Result(
        string Title, string Slug, string Excerpt, string AuthorName,
        string? CoverImageUrl, string BodyHtml, string MetaTitle, string MetaDescription);

    // Brand palette (tailwind.config.js): brand-600 for accent text, brand-500 for ornaments.
    private const string Accent = "#c90278";
    private const string AccentLight = "#ed028b";

    public const string Author = "The Sterlin Glams Atelier";

    /// <summary>Editorial angles. Passing a null/blank theme to <see cref="Generate"/> picks one by seed.</summary>
    public static readonly string[] Themes =
    {
        "The Season's Edit", "Everyday Gold", "The Gifting Edit", "Statement Pieces", "Worn Your Way",
        "New Arrivals", "The Layering Edit", "Gold Standard", "Occasion Ready", "The Bridal Edit",
        "Quiet Sparkle", "Weekend Gold", "Date-Night Jewels", "The Anniversary Edit"
    };

    private static readonly string[] TitleLeads =
    {
        "Gilded Hours", "Objects of Desire", "A Study in Gold", "Light &amp; Line",
        "The Curated Few", "Quiet Luxury", "The Art of Adornment", "Golden Hour",
        "In Good Company", "Second Skin", "The Finer Things", "Made to Be Worn",
        "Notes on Gold", "Everyday Icons", "A Certain Glow", "The Considered Edit"
    };

    private static readonly string[] Ornaments = { "&#10022;", "&#10023;", "&#8258;" }; // ✦ ✧ ⁂

    private static readonly string[] Intros =
    {
        "There is a particular kind of quiet confidence that comes from jewellery chosen with intention &mdash; not the loudest piece in the room, but the one that lingers in memory. This season, the Sterlin Glams atelier gathers {0} pieces designed to be worn together, or drawn apart and made entirely your own.",
        "The finest looks are built from a few deliberate choices. We&rsquo;ve gathered {0} pieces from the collection &mdash; each one a small act of glamour, ready to slip into your everyday or carry you through the evening.",
        "Adornment is a language, and gold is its softest word. Here are {0} of our favourites this season: pieces to layer, to gift, and to make unmistakably your own.",
        "Some pieces announce themselves; others simply feel like you. In this edit we&rsquo;ve set {0} of them side by side &mdash; a small collection with a lot to say, and nothing to prove.",
        "Getting dressed is a kind of storytelling, and jewellery writes the final line. These {0} pieces are the ones we keep reaching for &mdash; the finishing touches that turn an outfit into a look.",
        "Luxury, we think, is in the details you return to. We&rsquo;ve pulled {0} pieces we love right now &mdash; each one designed to be lived in, layered, and loved for years."
    };

    private static readonly string[] SectionHeadings =
    {
        "The Statement", "The Everyday Heirloom", "The Romance", "For the Evening", "Everyday Gold",
        "The Finishing Touch", "The Icon", "Worn Your Way", "The Quiet One", "Day to Night",
        "The Layered Look", "First Impressions", "The Keepsake", "Effortless Gold", "A Little Drama",
        "The Understatement", "Golden Hour", "The Pairing"
    };

    // {0} and {1} are pre-formatted product mentions (brand-coloured name + price). Deliberately
    // varied in tone — poetic, confident, warm, playful, minimal — so posts never read the same.
    private static readonly string[] LeadsTwo =
    {
        "Begin where every unforgettable look begins &mdash; with a single, deliberate flourish. The {0} needs no introduction, while the {1} answers in kind: modern, fluid, and unmistakably you.",
        "The truest luxuries are the ones we reach for without thinking. Slip on the {0}, and let the {1} catch the afternoon light &mdash; worn from morning to midnight until they quietly become a part of you.",
        "For the days that call for softness, the {0} arrives like a whisper, while the {1} traces the line of your look like a line of poetry.",
        "Pair the {0} with the {1} and the whole outfit falls into place &mdash; two pieces in easy conversation, neither trying too hard.",
        "Consider this your uniform for the season: the {0} for a little shine, the {1} for a little swagger. Together, they do all the talking.",
        "There is nothing accidental about looking effortless. Start with the {0}, add the {1}, and let a considered pairing read as pure instinct.",
        "Gold loves company. The {0} and the {1} were made to be worn as a pair &mdash; layered, mismatched, and entirely up to you.",
        "Some pieces are for being seen; others, for being remembered. The {0} does the first beautifully, and the {1} takes care of the rest.",
        "Dress it up or wear it with denim &mdash; the {0} and the {1} were built to move with you, from the school run to the dinner reservation.",
        "This is the pairing we keep coming back to: the {0} for structure, the {1} for shine. Classic, but never quiet.",
        "Let the {0} set the tone and the {1} finish the sentence. Restraint, it turns out, is its own kind of glamour."
    };

    private static readonly string[] LeadsOne =
    {
        "And for the moment that asks for a little more, the {0} answers &mdash; architectural, luminous, and quietly modern.",
        "Let the {0} lead. Worn alone, against bare skin, it says everything and asks for nothing.",
        "The {0} is the piece you&rsquo;ll return to &mdash; a small, everyday indulgence that never feels ordinary.",
        "If you buy one thing this season, make it the {0}. It is the kind of piece that quietly earns its keep.",
        "The {0} needs no styling notes. Put it on, and let it be the whole story.",
        "Consider the {0} a love letter to the details &mdash; understated, deliberate, and entirely yours."
    };

    private static readonly string[] Notes =
    {
        "Let one piece lead. Pair a statement set with bare ears, or a bold ring with the simplest chain. Elegance, after all, is knowing what to leave unsaid.",
        "Mix metals with confidence and layer by length, not by rule. The most memorable looks are the ones that feel effortless.",
        "Every set is designed to divide and be re-imagined &mdash; take the ring from a suite and slip it into your everyday stack.",
        "Odd numbers flatter: three rings, five bangles, one unexpected earring. Symmetry is lovely, but a little imbalance is what people remember.",
        "Start with the piece you love most and build outward. The rest will follow, and nothing should ever feel like a rule.",
        "Wear the good jewellery on the ordinary days. The occasion, more often than not, is simply being yourself."
    };

    private static readonly string[] WearYourWay =
    {
        "Every piece here is designed to be layered, mixed, and made your own &mdash; take a ring from a suite for your everyday stack, or lift a pair of earrings and wear them entirely alone. The collection is yours to compose.",
        "None of this is meant to stay in the box. Stack it, layer it, mismatch it on purpose. These pieces were made to be lived in, not saved for later.",
        "Think of this edit as a starting point, not a set of rules. Pull it apart, pair it with what you already own, and let it become unmistakably yours.",
        "The joy of gold is in the mixing. Take what speaks to you, leave the rest, and build a look that could only be yours."
    };

    private static readonly string[] Closings =
    {
        "Discover the full collection in-store and online at Sterlin Glams.",
        "Find these pieces &mdash; and the ones still to become your favourites &mdash; at Sterlin Glams.",
        "Visit us in-store or online to make any of these your own.",
        "Explore the edit in full at Sterlin Glams, in-store and online.",
        "See these and more at any Sterlin Glams store, or shop the collection online."
    };

    private static readonly string[] Excerpts =
    {
        "An intimate edit of {0} pieces chosen to be worn together &mdash; or drawn apart and made entirely your own.",
        "A curated look at {0} of the season&rsquo;s most-loved pieces, from the Sterlin Glams atelier.",
        "{0} favourites to layer, to gift, and to wear your way &mdash; curated by the Sterlin Glams atelier.",
        "The pieces we can&rsquo;t stop reaching for: {0} favourites, styled and ready to be made your own.",
        "{0} considered pieces, one elegant edit &mdash; a little inspiration from the Sterlin Glams atelier."
    };

    private static string Pick(string[] pool, int seed, int salt) => pool[Math.Abs(seed + salt * 97) % pool.Length];

    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string Mention(Featured p) =>
        $"<strong style=\"color:{Accent};\">{Esc(p.Name)}</strong> <em>(&#8358;{p.Price:N0})</em>";

    /// <summary>Rewrite a raw Cloudinary URL to a wide, content-aware hero crop (16:9). Non-Cloudinary
    /// or already-transformed URLs are returned unchanged.</summary>
    private static string? WideCover(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        const string marker = "/image/upload/";
        var i = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return url;
        var at = i + marker.Length;
        var end = url.IndexOf('/', at);
        var firstSeg = end < 0 ? url[at..] : url[at..end];
        // Don't double-wrap a URL that already carries a transform block.
        if (firstSeg.Contains(',') || firstSeg.StartsWith("f_") || firstSeg.StartsWith("w_")
            || firstSeg.StartsWith("q_") || firstSeg.StartsWith("c_"))
            return url;
        const string t = "c_fill,g_auto,f_auto,q_auto,w_1600,h_900";
        return url[..at] + t + "/" + url[at..];
    }

    /// <summary>Build a complete post. <paramref name="products"/> should hold ~4–6 featured items.</summary>
    public Result Generate(int seed, IReadOnlyList<Featured> products, string? theme = null)
    {
        var picks = products.Take(6).ToList();
        if (picks.Count == 0)
            picks.Add(new Featured("our latest arrivals", 0, null, "Jewellery"));

        theme = string.IsNullOrWhiteSpace(theme) ? Pick(Themes, seed, 3) : theme.Trim();
        var titleLead = Pick(TitleLeads, seed, 1);
        var title = $"{titleLead}: {theme}";
        var ornament = Pick(Ornaments, seed, 13);

        var body = new StringBuilder();
        // Brand-coloured kicker.
        body.Append($"<p style=\"text-align:center;font-style:italic;color:{Accent};\">{Esc(theme)}</p>");
        body.Append($"<p>{string.Format(Pick(Intros, seed, 2), picks.Count)}</p>");

        // Two products per section, up to three sections. Each section pulls its heading and lead
        // from a different index so the voice shifts as the post goes on.
        var sections = 0;
        for (int i = 0; i < picks.Count && sections < 3; i += 2, sections++)
        {
            var heading = Pick(SectionHeadings, seed, sections + 5);
            body.Append($"<h2>{Esc(heading)}</h2>");
            string para = i + 1 < picks.Count
                ? string.Format(Pick(LeadsTwo, seed, sections + 11), Mention(picks[i]), Mention(picks[i + 1]))
                : string.Format(Pick(LeadsOne, seed, sections + 11), Mention(picks[i]));
            body.Append($"<p>{para}</p>");
            if (i + 2 < picks.Count && sections < 2)
                body.Append($"<p style=\"text-align:center;color:{AccentLight};font-size:1.25rem;\">{ornament}</p>");
        }

        body.Append($"<blockquote><span style=\"color:{Accent};font-weight:600;\">A styling note.</span> {Pick(Notes, seed, 4)}</blockquote>");
        body.Append("<h3>Wear it your way</h3>");
        body.Append($"<p>{Pick(WearYourWay, seed, 7)}</p>");
        body.Append($"<p style=\"text-align:center;font-style:italic;color:#737373;\">{Pick(Closings, seed, 6)}</p>");

        var excerpt = string.Format(Pick(Excerpts, seed, 8), picks.Count);
        var cover = WideCover(picks.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.ImageUrl))?.ImageUrl);
        var slug = Slugify(title);

        // Excerpt/meta are plain text — decode the few HTML entities used above.
        var plainExcerpt = System.Net.WebUtility.HtmlDecode(excerpt);

        return new Result(
            Title: System.Net.WebUtility.HtmlDecode(title),
            Slug: slug,
            Excerpt: plainExcerpt,
            AuthorName: Author,
            CoverImageUrl: cover,
            BodyHtml: body.ToString(),
            MetaTitle: $"{System.Net.WebUtility.HtmlDecode(title)} | Sterlin Glams Journal",
            MetaDescription: plainExcerpt);
    }

    private static string Slugify(string s)
    {
        s = System.Net.WebUtility.HtmlDecode(s);
        return System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant().Trim(), "[^a-z0-9]+", "-").Trim('-');
    }
}
