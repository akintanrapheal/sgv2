using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using System.Text.RegularExpressions;

namespace SterlingLams.Web.Services;

public interface IWooCommerceImportService
{
    Task<WooImportResult> ImportFromCsvAsync(Stream csvStream);
}

public class WooImportResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool Success => !Errors.Any();
    public string Summary =>
        $"{Created} created, {Updated} updated, {Skipped} skipped" +
        (Errors.Any() ? $", {Errors.Count} errors" : "");
}

public class WooCommerceImportService : IWooCommerceImportService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<WooCommerceImportService> _logger;

    // WooCommerce category keyword → local category slug (first match wins)
    private static readonly (string Fragment, string Slug)[] CategoryMap =
    {
        ("Earrings",         "earrings"),
        ("Necklaces",        "necklaces"),
        ("Pendants",         "necklaces"),
        ("Rings",            "rings"),
        ("Bracelets",        "bracelets"),
        ("Bangles",          "bracelets"),
        ("Sets",             "sets"),
        ("Clutches",         "clutches"),
        ("Sunglasses",       "sunglasses"),
        ("Brooches",         "brooches"),
        ("Waist Chains",     "accessories"),
        ("Hair Accessories", "accessories"),
        ("Cufflinks",        "accessories"),
        ("Scarfs",           "accessories"),
        ("Caps",             "accessories"),
        ("Watches",          "watches"),
        ("Mens",             "accessories"),
    };

    public WooCommerceImportService(ApplicationDbContext db, ILogger<WooCommerceImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<WooImportResult> ImportFromCsvAsync(Stream csvStream)
    {
        var result = new WooImportResult();

        // Clear all existing products (and their variants/images) before importing
        var toDelete = await _db.Products
            .Include(p => p.Images)
            .Include(p => p.Variants)
            .ToListAsync();
        _db.Products.RemoveRange(toDelete);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Cleared {Count} existing products before WooCommerce import.", toDelete.Count);

        var categories = await _db.Categories.ToListAsync();
        var existingByCode = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);

        // Track slugs used in this import run so we don't assign duplicates within a batch
        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Load the Colour attribute and all its values for variant creation
        var colourAttr = await _db.ProductAttributes
            .Include(a => a.Values)
            .FirstOrDefaultAsync(a => a.Slug == "colour");

        var colourValues = colourAttr?.Values.ToList()
                        ?? new List<ProductAttributeValue>();

        var rows = ParseCsv(csvStream);
        var published = rows.Where(r => Get(r, "post_status") == "Published").ToList();

        int batchCount = 0;

        foreach (var row in published)
        {
            try
            {
                var name = System.Net.WebUtility.HtmlDecode(Get(row, "post_title"));
                if (string.IsNullOrWhiteSpace(name)) { result.Skipped++; continue; }

                var priceStr = Get(row, "regular_price");
                if (!decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var price) || price <= 0)
                {
                    result.Skipped++;
                    continue;
                }

                var sku  = Get(row, "sku");
                var id   = Get(row, "ID");
                var code = "WC-" + (string.IsNullOrWhiteSpace(sku) ? id : sku);

                var category = ResolveCategory(Get(row, "tax:product_cat"), categories)
                            ?? await EnsureCategoryAsync("Accessories", "accessories", categories);

                var description = StripHtml(Get(row, "post_content"));
                var shortDesc   = StripHtml(Get(row, "post_excerpt"));
                var imageUrls   = ParseImageUrls(Get(row, "images"));

                // pa_color is the main colour field — pipe-separated for multi-colour products
                // e.g. "Gold|Silver" means two variants; "Gold/Silver" is a single two-tone option
                var paColor  = Get(row, "attribute:pa_color");
                var colNames = string.IsNullOrWhiteSpace(paColor)
                    ? Array.Empty<string>()
                    : paColor.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // Use first colour as the product's Metal field (for display in Details)
                var firstColour = colNames.FirstOrDefault() ?? Get(row, "attribute:Colour");

                var slug    = await UniqueSlugAsync(Slugify(name), usedSlugs);
                usedSlugs.Add(slug);
                var product = new Product
                {
                    ExternalCode     = code,
                    Sku              = sku,
                    Name             = name,
                    Slug             = slug,
                    Price            = price,
                    Description      = description,
                    ShortDescription = shortDesc,
                    Metal            = firstColour,
                    IsActive         = true,
                    CategoryId       = category.Id,
                    CreatedAt        = DateTime.UtcNow,
                    UpdatedAt        = DateTime.UtcNow,
                };

                var sort = 0;
                foreach (var url in imageUrls)
                {
                    sort++;
                    product.Images.Add(new ProductImage { Url = url, IsPrimary = sort == 1, SortOrder = sort });
                }

                // Create a variant per colour option
                if (colNames.Length > 0 && colourAttr != null)
                {
                    foreach (var colName in colNames)
                    {
                        // Find exact match first, then case-insensitive, then create new value
                        var attrVal = colourValues.FirstOrDefault(v =>
                                          v.Value.Equals(colName, StringComparison.Ordinal))
                                   ?? colourValues.FirstOrDefault(v =>
                                          v.Value.Equals(colName, StringComparison.OrdinalIgnoreCase));

                        if (attrVal == null)
                        {
                            // Create new colour value on the fly
                            attrVal = new ProductAttributeValue
                            {
                                AttributeId = colourAttr.Id,
                                Value       = colName,
                                SortOrder   = colourValues.Count + 1
                            };
                            _db.ProductAttributeValues.Add(attrVal);
                            colourValues.Add(attrVal);
                            await _db.SaveChangesAsync(); // need the ID before linking
                        }

                        product.Variants.Add(new ProductVariant
                        {
                            Name            = colName,
                            IsActive        = true,
                            StockQuantity   = 0,
                            AttributeValues = new List<ProductAttributeValue> { attrVal }
                        });
                    }
                }

                _db.Products.Add(product);
                await _db.SaveChangesAsync();   // save each product individually — avoids slug/key collisions within a batch
                existingByCode[code] = product;
                result.Created++;
                batchCount++;
            }
            catch (Exception ex)
            {
                // Surface the innermost exception for DB constraint errors
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                var msg = $"Error importing '{Get(row, "post_title")}': {inner.Message}";
                _logger.LogWarning(ex, msg);
                result.Errors.Add(msg);
                result.Skipped++;

                // Detach any partially-tracked entities so the next product can still save
                _db.ChangeTracker.Clear();
            }
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            _logger.LogError(ex, "Final SaveChanges failed: {Message}", inner.Message);
            result.Errors.Add($"Final save error: {inner.Message}");
        }

        // Ensure every product has a StoreInventory record for every active store (qty = 0)
        await EnsureStoreInventoryRecordsAsync();

        _logger.LogInformation("WooCommerce CSV import complete: {Summary}", result.Summary);
        return result;
    }

    private async Task EnsureStoreInventoryRecordsAsync()
    {
        var productIds = await _db.Products.Select(p => p.Id).ToListAsync();
        var storeIds   = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();

        var existing = await _db.StoreInventories
            .Select(si => new { si.ProductId, si.StoreId })
            .ToListAsync();

        var existingSet = existing.Select(e => (e.ProductId, e.StoreId)).ToHashSet();

        var toCreate = new List<StoreInventory>();
        foreach (var pid in productIds)
            foreach (var sid in storeIds)
                if (!existingSet.Contains((pid, sid)))
                    toCreate.Add(new StoreInventory { ProductId = pid, StoreId = sid, QuantityOnHand = 0, UpdatedAt = DateTime.UtcNow });

        if (toCreate.Any())
        {
            _db.StoreInventories.AddRange(toCreate);
            await _db.SaveChangesAsync();
            _logger.LogInformation("Created {Count} StoreInventory records (stock = 0).", toCreate.Count);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<Dictionary<string, string>> ParseCsv(Stream stream)
    {
        var rows = new List<Dictionary<string, string>>();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        using var parser = new TextFieldParser(reader)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
        };
        parser.SetDelimiters(",");

        if (parser.EndOfData) return rows;

        var rawHeaders = parser.ReadFields() ?? Array.Empty<string>();
        var headers    = DeduplicateHeaders(rawHeaders);

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? Array.Empty<string>();
            var row    = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < headers.Length && i < fields.Length; i++)
                row[headers[i]] = fields[i];
            rows.Add(row);
        }
        return rows;
    }

    private static string[] DeduplicateHeaders(string[] raw)
    {
        var result = new string[raw.Length];
        var seen   = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < raw.Length; i++)
        {
            var h = raw[i];
            if (seen.TryGetValue(h, out var n)) { seen[h]++; result[i] = $"{h}_{n}"; }
            else                                { seen[h] = 1;    result[i] = h; }
        }
        return result;
    }

    private Category? ResolveCategory(string? wooCategory, List<Category> categories)
    {
        if (string.IsNullOrWhiteSpace(wooCategory)) return null;
        foreach (var (fragment, slug) in CategoryMap)
        {
            if (wooCategory.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return categories.FirstOrDefault(c => c.Slug == slug);
        }
        return null;
    }

    private async Task<Category> EnsureCategoryAsync(string name, string slug, List<Category> categories)
    {
        var cat = categories.FirstOrDefault(c => c.Slug == slug);
        if (cat != null) return cat;
        cat = new Category { Name = name, Slug = slug, IsActive = true };
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();
        categories.Add(cat);
        return cat;
    }

    private static List<string> ParseImageUrls(string? imageField)
    {
        if (string.IsNullOrWhiteSpace(imageField)) return new();
        return Regex.Matches(imageField, @"https?://[^\s,!""]+")
                    .Select(m => m.Value.TrimEnd(',', ' '))
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct()
                    .ToList();
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Preserve line breaks: turn block-level/break tags into newlines first.
        var withBreaks = Regex.Replace(html, @"<\s*(br|/p|/div|/li|/h[1-6])\s*/?>", "\n",
            RegexOptions.IgnoreCase);
        var stripped = Regex.Replace(withBreaks, "<[^>]*>", " ");

        // Decode HTML entities (&amp; &rsquo; &#8217; etc.).
        var decoded = System.Net.WebUtility.HtmlDecode(stripped);

        // Collapse spaces/tabs but keep newlines; trim blank lines.
        decoded = Regex.Replace(decoded, @"[ \t]+", " ");
        decoded = Regex.Replace(decoded, @"\n{3,}", "\n\n");
        var lines = decoded.Split('\n').Select(l => l.Trim());
        return string.Join("\n", lines).Trim();
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, HashSet<string>? inMemory = null)
    {
        var slug = baseSlug;
        var n = 1;
        // Check both the DB and slugs already assigned in this import run (unsaved)
        while (await _db.Products.AnyAsync(p => p.Slug == slug) ||
               (inMemory != null && inMemory.Contains(slug)))
        {
            slug = $"{baseSlug}-{n++}";
        }
        return slug;
    }

    private static string Slugify(string name) =>
        Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');

    private static string Get(Dictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
}
