namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// The single source of truth for how storefront categories are grouped. Used by the top
/// navigation mega-menu (<c>_Navigation.cshtml</c>) and the shop-page sidebar accordion
/// (<c>_CategorySidebar.cshtml</c>) so the two never drift apart. Edit the groups here.
/// </summary>
public static class StoreMenu
{
    /// <summary>A top-level menu entry. Empty <see cref="Items"/> = a plain link (uses <see cref="Href"/>);
    /// otherwise it's a group whose items are (label, category-slug) pairs.</summary>
    public record Entry(string Label, string? Href, IReadOnlyList<(string Label, string Slug)> Items);

    /// <summary>A rendered sidebar node: a group (with <see cref="Children"/>) or, when Children is empty,
    /// a standalone category link (via <see cref="Slug"/>).</summary>
    public record SidebarNode(string Label, string? Slug, IReadOnlyList<(string Label, string Slug)> Children);

    private static (string, string)[] G(params (string, string)[] items) => items;

    public static readonly IReadOnlyList<Entry> Entries = new List<Entry>
    {
        new("New In", "/Products?sortBy=newest", System.Array.Empty<(string, string)>()),
        new("Jewelry", null, G(("Sets", "sets"), ("Earrings", "earrings"), ("Rings", "rings"),
            ("Bracelet & Bangle", "bracelets"), ("Necklaces", "necklaces"))),
        new("Accessories", null, G(("Anklets", "anklets"), ("Waist Chains", "waist-chains"), ("Key Rings", "key-rings"),
            ("Hair Accessories", "hair-accessories"), ("Scarfs & Caps", "scarfs-caps"), ("Belts", "belts"),
            ("Sunglasses", "sunglasses"), ("Brooches", "brooches"), ("Extenders", "extenders"))),
        new("Watches", null, G(("Bracelet Watches", "bracelet-watches"), ("Strap Watches", "strap-watches"))),
        new("Gifts", null, G(("Gift Boxes", "gift-boxes"), ("Gifts Combo Packages", "gifts-combo-packages"))),
        new("Mens", null, G(("Bracelet", "mens-bracelets"), ("Cufflinks", "cufflinks"), ("Unisex Brooches", "unisex-brooches"),
            ("Trouser Chains", "trouser-chains"), ("MenKind", "menkind"))),
        new("Clutches", null, G(("Stoned Clutches", "stoned-clutches"), ("None Stone Clutches", "none-stone-clutches"))),
    };

    /// <summary>
    /// Expands a category slug for the shop listing. If the slug is a menu-group parent (e.g. the
    /// "clutches" category behind the "All Clutches" link), returns that slug PLUS every sub-category
    /// slug in the group, so the parent listing shows all products across the group (stoned + none-stone,
    /// etc.). A leaf category, or any slug not a group parent, returns just itself. The parent slug is
    /// the group label lower-cased — the same rule <see cref="BuildSidebar"/> uses to surface "All X".
    /// </summary>
    public static IReadOnlyList<string> ExpandSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return System.Array.Empty<string>();
        foreach (var e in Entries)
        {
            if (e.Items.Count == 0) continue; // link-only entry (e.g. "New In")
            if (string.Equals(e.Label.ToLowerInvariant(), slug, System.StringComparison.OrdinalIgnoreCase))
            {
                var slugs = new List<string> { slug };
                slugs.AddRange(e.Items.Select(i => i.Slug));
                return slugs;
            }
        }
        return new[] { slug };
    }

    /// <summary>
    /// Builds the shop-page sidebar tree from the categories that actually have products right now.
    /// Groups keep only their in-stock sub-items and are dropped when empty; any active category not
    /// placed in a group is added as a standalone link. The whole list is sorted alphabetically to
    /// match the old-site "Shop Categories" panel.
    /// </summary>
    public static IReadOnlyList<SidebarNode> BuildSidebar(IEnumerable<(string Slug, string Name)> activeCategories)
    {
        var active = new Dictionary<string, string>();
        foreach (var c in activeCategories) active[c.Slug] = c.Name;

        var nodes = new List<SidebarNode>();
        var grouped = new HashSet<string>();

        foreach (var e in Entries)
        {
            foreach (var it in e.Items) grouped.Add(it.Slug);
            if (e.Items.Count == 0) continue; // link-only entry (e.g. "New In") — not a category group
            var children = e.Items.Where(i => active.ContainsKey(i.Slug)).ToList();

            // Some group names are also real categories that hold products directly (e.g. the
            // "Accessories" category vs the Accessories group). Surface that parent as an "All <group>"
            // link at the top of the group rather than a duplicate top-level entry.
            var parentSlug = e.Label.ToLowerInvariant();
            if (active.ContainsKey(parentSlug))
            {
                grouped.Add(parentSlug);
                children.Insert(0, ("All " + e.Label, parentSlug));
            }

            if (children.Count == 0) continue;
            nodes.Add(new SidebarNode(e.Label, null, children));
        }

        foreach (var kv in active)
            if (!grouped.Contains(kv.Key))
                nodes.Add(new SidebarNode(kv.Value, kv.Key, System.Array.Empty<(string, string)>()));

        return nodes.OrderBy(n => n.Label, System.StringComparer.OrdinalIgnoreCase).ToList();
    }
}
