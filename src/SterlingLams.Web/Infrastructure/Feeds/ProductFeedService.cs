using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Infrastructure.Feeds;

/// <summary>
/// Builds the product feed shared by every shopping channel (Google Merchant Center, Meta catalogue).
/// One entry per ACTIVE, in-catalogue product — using the sale-aware effective price, aggregate stock
/// for availability, and the primary image. Products with no price or no image are skipped (both are
/// required by Google and would otherwise be rejected item-by-item). Per-variant grouping (item_group_id)
/// is a deliberate follow-up: one item per product is fully valid and gets the catalogue live fast.
/// </summary>
public interface IProductFeedService
{
    Task<List<FeedItem>> BuildItemsAsync(CancellationToken ct = default);
}

public sealed class ProductFeedService : IProductFeedService
{
    private readonly ApplicationDbContext _db;
    public ProductFeedService(ApplicationDbContext db) => _db = db;

    public async Task<List<FeedItem>> BuildItemsAsync(CancellationToken ct = default)
    {
        var baseUrl = SeoUrl.Base;
        var now = DateTime.UtcNow;

        // Category names (+ parent) for product_type and the Google category guess — one small query.
        var cats = await _db.Categories.AsNoTracking()
            .Select(c => new { c.Id, c.Name, ParentName = c.Parent != null ? c.Parent.Name : null })
            .ToListAsync(ct);
        var catById = cats.ToDictionary(c => c.Id, c => (c.Name, c.ParentName));

        var rows = await _db.Products.AsNoTracking()
            .Where(p => p.IsActive && !p.IsArchived && !p.IsCustomItem)
            .Select(p => new
            {
                p.Id, p.Name, p.Slug, p.Description, p.ShortDescription,
                p.Price, p.SalePrice, p.SaleStartsAt, p.SaleEndsAt,
                p.Sku, p.Barcode, p.ExternalCode, p.Material, p.Metal, p.CategoryId, p.TrackStock,
                Images = p.Images.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.SortOrder)
                    .Select(i => i.Url).ToList(),
                Stock = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0
            })
            .ToListAsync(ct);

        var items = new List<FeedItem>(rows.Count);
        foreach (var p in rows)
        {
            var regular = p.Price;
            if (regular <= 0m) continue;                                   // Google rejects price <= 0

            var rawPrimary = p.Images.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            if (string.IsNullOrWhiteSpace(rawPrimary)) continue;           // image_link is required

            var onSale = p.SalePrice is decimal s && s > 0m && s < regular
                         && (p.SaleStartsAt == null || p.SaleStartsAt <= now)
                         && (p.SaleEndsAt == null || p.SaleEndsAt >= now);

            var inStock = !p.TrackStock || p.Stock > 0;

            // Images: a right-sized WebP rendition keeps Google's crawl light; fall back to the raw URL.
            string Img1200(string u) => Abs(Img.Cld(u, 1200) ?? u, baseUrl);
            var image = Img1200(rawPrimary);
            var extras = p.Images.Where(u => !string.IsNullOrWhiteSpace(u) && u != rawPrimary)
                .Take(9).Select(Img1200).ToList();

            catById.TryGetValue(p.CategoryId, out var cat);
            var productType = cat.ParentName is { Length: > 0 }
                ? $"{cat.ParentName} > {cat.Name}" : cat.Name;

            var gtin = IsValidGtin(p.Barcode) ? p.Barcode!.Trim() : null;
            var mpn = FirstNonBlank(p.Sku, p.ExternalCode);

            items.Add(new FeedItem
            {
                Id = $"p{p.Id}",
                Title = Clip(p.Name, 150),
                Description = Clip(PlainText(FirstNonBlank(p.Description, p.ShortDescription, p.Name)!), 4900),
                Link = $"{baseUrl}/products/{p.Slug}",
                ImageLink = image,
                AdditionalImageLinks = extras,
                InStock = inStock,
                Price = regular,
                SalePrice = onSale ? p.SalePrice : null,
                Gtin = gtin,
                Mpn = mpn,                                // kept even when a GTIN exists; both are allowed
                GoogleProductCategory = GuessGoogleCategory(cat.Name, cat.ParentName),
                ProductType = string.IsNullOrWhiteSpace(productType) ? null : productType,
                Material = FirstNonBlank(p.Material, p.Metal)
            });
        }
        return items;
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────
    private static string Abs(string url, string baseUrl) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : baseUrl + url;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    private static readonly Regex TagRx = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WsRx = new(@"\s+", RegexOptions.Compiled);
    /// <summary>HTML → readable plain text (strip tags, decode entities, collapse whitespace).</summary>
    private static string PlainText(string html)
    {
        var text = System.Net.WebUtility.HtmlDecode(TagRx.Replace(html, " "));
        return WsRx.Replace(text, " ").Trim();
    }

    /// <summary>A barcode we can present as a GTIN: digits only, of a standard GTIN length.</summary>
    private static bool IsValidGtin(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return false;
        var d = barcode.Trim();
        return d.All(char.IsDigit) && d.Length is 8 or 12 or 13 or 14;
    }

    /// <summary>Google product taxonomy id. Watches and bags get their own nodes (a clutch filed under
    /// Jewelry gets disapproved); everything else defaults to Jewelry, correct for the bulk of the catalogue.</summary>
    private static string GuessGoogleCategory(string? name, string? parent)
    {
        var hay = $"{name} {parent}".ToLowerInvariant();
        if (hay.Contains("watch")) return "201";    // Apparel & Accessories > Jewelry > Watches
        if (hay.Contains("clutch") || hay.Contains("bag") || hay.Contains("purse")
            || hay.Contains("handbag") || hay.Contains("wallet"))
            return "6551";                          // Apparel & Accessories > Handbags, Wallets & Cases > Handbags
        return "188";                               // Apparel & Accessories > Jewelry
    }
}
