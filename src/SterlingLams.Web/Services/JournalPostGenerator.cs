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
    private const string Ornament = "&#10022;"; // ✦

    public const string Author = "The Sterlin Glams Atelier";

    /// <summary>Editorial angles. Passing a null/blank theme to <see cref="Generate"/> picks one by seed.</summary>
    public static readonly string[] Themes =
    {
        "The Season's Edit", "Everyday Gold", "The Gifting Edit", "Statement Pieces", "Worn Your Way"
    };

    private static readonly string[] TitleLeads =
    {
        "Gilded Hours", "Objects of Desire", "A Study in Gold", "Light &amp; Line",
        "The Curated Few", "Quiet Luxury", "The Art of Adornment", "Golden Hour"
    };

    private static readonly string[] Intros =
    {
        "There is a particular kind of quiet confidence that comes from jewellery chosen with intention &mdash; not the loudest piece in the room, but the one that lingers in memory. This season, the Sterlin Glams atelier gathers {0} pieces designed to be worn together, or drawn apart and made entirely your own.",
        "The finest looks are built from a few deliberate choices. We&rsquo;ve gathered {0} pieces from the collection &mdash; each one a small act of glamour, ready to slip into your everyday or carry you through the evening.",
        "Adornment is a language, and gold is its softest word. Here are {0} of our favourites this season: pieces to layer, to gift, and to make unmistakably your own."
    };

    private static readonly string[] SectionHeadings =
    {
        "The Statement", "The Everyday Heirloom", "The Romance", "For the Evening",
        "Everyday Gold", "The Finishing Touch", "The Icon", "Worn Your Way"
    };

    // {0} and {1} are pre-formatted product mentions (brand-coloured name + price).
    private static readonly string[] LeadsTwo =
    {
        "Begin where every unforgettable look begins &mdash; with a single, deliberate flourish. The {0} needs no introduction, while the {1} answers in kind: modern, fluid, and unmistakably you.",
        "The truest luxuries are the ones we reach for without thinking. Slip on the {0}, and let the {1} catch the afternoon light &mdash; worn from morning to midnight until they quietly become a part of you.",
        "For the days that call for softness, the {0} blooms in a single gesture, while the {1} traces the neckline like a line of poetry.",
        "Pair the {0} with the {1} for a look that feels considered rather than composed &mdash; two pieces in easy conversation."
    };

    private static readonly string[] LeadsOne =
    {
        "And for the moment that asks for a little more, the {0} answers &mdash; architectural, luminous, and quietly modern.",
        "Let the {0} lead. Worn alone, against bare skin, it says everything and asks for nothing.",
        "The {0} is the piece you&rsquo;ll return to &mdash; a small, everyday indulgence that never feels ordinary."
    };

    private static readonly string[] Notes =
    {
        "Let one piece lead. Pair a statement set with bare ears, or a bold ring with the simplest chain. Elegance, after all, is knowing what to leave unsaid.",
        "Mix metals with confidence and layer by length, not by rule. The most memorable looks are the ones that feel effortless.",
        "Every set is designed to divide and be re-imagined &mdash; take the ring from a suite and slip it into your everyday stack."
    };

    private static readonly string[] Closings =
    {
        "Discover the full collection in-store and online at Sterlin Glams.",
        "Find these pieces &mdash; and the ones still to become your favourites &mdash; at Sterlin Glams.",
        "Visit us in-store or online to make any of these your own."
    };

    private static readonly string[] Excerpts =
    {
        "An intimate edit of {0} pieces chosen to be worn together &mdash; or drawn apart and made entirely your own.",
        "A curated look at {0} of the season&rsquo;s most-loved pieces, from the Sterlin Glams atelier.",
        "{0} favourites to layer, to gift, and to wear your way &mdash; curated by the Sterlin Glams atelier."
    };

    private static string Pick(string[] pool, int seed, int salt) => pool[Math.Abs(seed + salt * 97) % pool.Length];

    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string Mention(Featured p) =>
        $"<strong style=\"color:{Accent};\">{Esc(p.Name)}</strong> <em>(&#8358;{p.Price:N0})</em>";

    /// <summary>Build a complete post. <paramref name="products"/> should hold ~4–6 featured items.</summary>
    public Result Generate(int seed, IReadOnlyList<Featured> products, string? theme = null)
    {
        var picks = products.Take(6).ToList();
        if (picks.Count == 0)
            picks.Add(new Featured("our latest arrivals", 0, null, "Jewellery"));

        theme = string.IsNullOrWhiteSpace(theme) ? Pick(Themes, seed, 3) : theme.Trim();
        var titleLead = Pick(TitleLeads, seed, 1);
        var title = $"{titleLead}: {theme}";

        var body = new StringBuilder();
        // Brand-coloured kicker.
        body.Append($"<p style=\"text-align:center;font-style:italic;color:{Accent};\">{Esc(theme)}</p>");
        body.Append($"<p>{string.Format(Pick(Intros, seed, 2), picks.Count)}</p>");

        // Two products per section, up to three sections.
        var sections = 0;
        for (int i = 0; i < picks.Count && sections < 3; i += 2, sections++)
        {
            var heading = Pick(SectionHeadings, seed, sections + 5);
            body.Append($"<h2>{Esc(heading)}</h2>");
            string para = i + 1 < picks.Count
                ? string.Format(Pick(LeadsTwo, seed, sections + 11), Mention(picks[i]), Mention(picks[i + 1]))
                : string.Format(Pick(LeadsOne, seed, sections + 11), Mention(picks[i]));
            body.Append($"<p>{para}</p>");
            // Ornament between sections (not after the last).
            if (i + 2 < picks.Count && sections < 2)
                body.Append($"<p style=\"text-align:center;color:{AccentLight};font-size:1.25rem;\">{Ornament}</p>");
        }

        body.Append($"<blockquote><span style=\"color:{Accent};font-weight:600;\">A styling note.</span> {Pick(Notes, seed, 4)}</blockquote>");
        body.Append("<h3>Wear it your way</h3>");
        body.Append("<p>Every piece here is designed to be layered, mixed, and made your own &mdash; take a ring from a suite for your everyday stack, or lift a pair of earrings and wear them entirely alone. The collection is yours to compose.</p>");
        body.Append($"<p style=\"text-align:center;font-style:italic;color:#737373;\">{Pick(Closings, seed, 6)}</p>");

        var excerpt = string.Format(Pick(Excerpts, seed, 8), picks.Count);
        var cover = picks.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.ImageUrl))?.ImageUrl;
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
