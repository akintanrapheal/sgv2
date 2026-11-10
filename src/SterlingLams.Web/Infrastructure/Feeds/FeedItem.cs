namespace SterlingLams.Web.Infrastructure.Feeds;

/// <summary>
/// One channel-agnostic product feed entry. Built once by <see cref="ProductFeedService"/> and then
/// serialised per channel (Google Merchant Center, Meta/Facebook catalogue) — both consume the Google
/// RSS 2.0 shape, differing only in a couple of tokens, so the same item set drives every feed.
/// </summary>
public sealed record FeedItem
{
    public required string Id { get; init; }
    /// <summary>Groups variants of one product (Google <c>item_group_id</c>). Null for standalone items.</summary>
    public string? ItemGroupId { get; init; }

    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Link { get; init; }
    public required string ImageLink { get; init; }
    public List<string> AdditionalImageLinks { get; init; } = new();

    public bool InStock { get; init; }

    /// <summary>Regular price (what Google calls <c>price</c>).</summary>
    public decimal Price { get; init; }
    /// <summary>Active promotional price, or null when not on sale (<c>sale_price</c>).</summary>
    public decimal? SalePrice { get; init; }
    public string Currency { get; init; } = "NGN";

    public string Brand { get; init; } = "Sterlin Glams";
    public string Condition { get; init; } = "new";

    /// <summary>A real GTIN/EAN/UPC barcode when we hold a valid one, else null.</summary>
    public string? Gtin { get; init; }
    /// <summary>Manufacturer part number — we use our SKU / external code.</summary>
    public string? Mpn { get; init; }

    /// <summary>Google's product taxonomy id (e.g. 188 = Apparel &amp; Accessories &gt; Jewelry).</summary>
    public string GoogleProductCategory { get; init; } = "188";
    /// <summary>Our own category path, e.g. "Necklaces &gt; Pendants" (merchant-defined).</summary>
    public string? ProductType { get; init; }
    public string? Material { get; init; }
}
