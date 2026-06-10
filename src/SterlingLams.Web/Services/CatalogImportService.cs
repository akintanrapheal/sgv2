using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface ICatalogImportService
{
    Task<CatalogImportResult> ImportAsync(string jsonPath, bool wipeFirst, IProgress<string>? progress = null);
}

public class CatalogImportResult
{
    public int Products { get; set; }
    public int Variants { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public string Summary => $"{Products} products, {Variants} variants, {Skipped} skipped" +
        (Errors.Count > 0 ? $", {Errors.Count} errors" : "");
}

/// <summary>
/// Imports the full product catalog (products + multi-attribute variants + images) from a
/// structured JSON file extracted from the legacy WooCommerce backup. Stock is imported as zero
/// (staff set per-branch quantities afterwards). Keyed on ExternalCode (WC-{sku}).
/// </summary>
public class CatalogImportService : ICatalogImportService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<CatalogImportService> _logger;

    public CatalogImportService(ApplicationDbContext db, ILogger<CatalogImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private record CatVariant(string? price, string? stock, Dictionary<string, string> attrs);
    private record CatProduct(
        string wc_id, string title, string sku, string? description, string? @short,
        string type, string? price, string? stock, List<string> categories,
        List<string> images, int variant_count, List<CatVariant> variants);

    // attribute slug → display name
    private static readonly Dictionary<string, string> AttrNames = new()
    {
        ["color"] = "Color", ["size"] = "Size", ["alphabet"] = "Alphabet",
        ["measurement"] = "Measurement", ["signs"] = "Signs", ["combo"] = "Combo",
    };

    public async Task<CatalogImportResult> ImportAsync(string jsonPath, bool wipeFirst, IProgress<string>? progress = null)
    {
        var result = new CatalogImportResult();
        void Log(string m) { progress?.Report(m); _logger.LogInformation("[catalog-import] {Msg}", m); }

        if (!File.Exists(jsonPath))
        {
            result.Errors.Add($"File not found: {jsonPath}");
            return result;
        }

        var products = JsonSerializer.Deserialize<List<CatProduct>>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        Log($"Parsed {products.Count} products from {Path.GetFileName(jsonPath)}.");

        if (wipeFirst)
        {
            // Raw delete lets the DB ON DELETE CASCADE clear variants, images, inventory and any
            // product-referencing rows in one shot.
            var n = await _db.Database.ExecuteSqlRawAsync("DELETE FROM \"Products\"");
            Log($"Wiped {n} existing product(s) (cascaded to variants/images/inventory).");
        }

        // Caches
        var categories = (await _db.Categories.ToListAsync()).ToDictionary(c => c.Slug, c => c, StringComparer.OrdinalIgnoreCase);
        var attrs = (await _db.ProductAttributes.Include(a => a.Values).ToListAsync())
            .ToDictionary(a => a.Slug, a => a, StringComparer.OrdinalIgnoreCase);
        var valueCache = new Dictionary<(int attrId, string val), ProductAttributeValue>();
        foreach (var a in attrs.Values)
            foreach (var v in a.Values)
                valueCache[(a.Id, v.Value.ToLowerInvariant())] = v;
        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task<Category> CategoryAsync(string name)
        {
            var slug = Slugify(name);
            if (string.IsNullOrEmpty(slug)) slug = "uncategorized";
            if (categories.TryGetValue(slug, out var c)) return c;
            c = new Category { Name = name, Slug = slug, IsActive = true };
            _db.Categories.Add(c);
            await _db.SaveChangesAsync();
            categories[slug] = c;
            return c;
        }

        async Task<ProductAttribute> AttributeAsync(string slug)
        {
            if (attrs.TryGetValue(slug, out var a)) return a;
            a = new ProductAttribute { Name = AttrNames.GetValueOrDefault(slug, Capitalize(slug)), Slug = slug, IsActive = true };
            _db.ProductAttributes.Add(a);
            await _db.SaveChangesAsync();
            attrs[slug] = a;
            return a;
        }

        async Task<ProductAttributeValue> ValueAsync(string attrSlug, string rawValue)
        {
            var attr = await AttributeAsync(attrSlug);
            var display = attrSlug == "alphabet" ? rawValue.ToUpperInvariant() : Capitalize(rawValue);
            var key = (attr.Id, display.ToLowerInvariant());
            if (valueCache.TryGetValue(key, out var v)) return v;
            v = new ProductAttributeValue { AttributeId = attr.Id, Value = display, SortOrder = attr.Values.Count + 1 };
            _db.ProductAttributeValues.Add(v);
            await _db.SaveChangesAsync();
            valueCache[key] = v;
            return v;
        }

        var uncategorized = await CategoryAsync("Uncategorized");
        // Only "Added" graphs are saved in the loop — skip change scanning of the growing tracked set.
        _db.ChangeTracker.AutoDetectChangesEnabled = false;
        int i = 0;
        foreach (var cp in products)
        {
            i++;
            if (i % 250 == 0) Log($"… {i}/{products.Count}");
            try
            {
                if (string.IsNullOrWhiteSpace(cp.title)) { result.Skipped++; continue; }
                decimal.TryParse(cp.price, NumberStyles.Any, CultureInfo.InvariantCulture, out var price);

                var category = cp.categories.Count > 0 ? await CategoryAsync(cp.categories[0]) : uncategorized;
                var slug = await UniqueSlugAsync(Slugify(cp.title), usedSlugs);
                usedSlugs.Add(slug);

                var product = new Product
                {
                    ExternalCode = "WC-" + (string.IsNullOrWhiteSpace(cp.sku) ? cp.wc_id : cp.sku),
                    Sku = cp.sku,
                    Name = cp.title,
                    Slug = slug,
                    Price = price,
                    Description = cp.description,
                    ShortDescription = cp.@short,
                    ProductType = cp.variants.Count > 0 ? "variable" : "simple",
                    IsActive = true,
                    CategoryId = category.Id,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                var sort = 0;
                foreach (var url in cp.images.Distinct())
                {
                    sort++;
                    product.Images.Add(new ProductImage { Url = url, IsPrimary = sort == 1, SortOrder = sort });
                }

                foreach (var v in cp.variants)
                {
                    var values = new List<ProductAttributeValue>();
                    foreach (var (attrSlug, raw) in v.attrs)
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        values.Add(await ValueAsync(attrSlug, raw));
                    }
                    if (values.Count == 0) continue;
                    decimal.TryParse(v.price, NumberStyles.Any, CultureInfo.InvariantCulture, out var vprice);
                    product.Variants.Add(new ProductVariant
                    {
                        Name = string.Join(" / ", values.Select(x => x.Value)),
                        PriceAdjustment = vprice > 0 ? vprice - price : null,
                        StockQuantity = 0,
                        IsActive = true,
                        AttributeValues = values
                    });
                    result.Variants++;
                }

                _db.ChangeTracker.DetectChanges(); // detect the newly-built graph only
                _db.Products.Add(product);
                await _db.SaveChangesAsync();
                result.Products++;
            }
            catch (Exception ex)
            {
                var inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
                result.Errors.Add($"'{cp.title}': {inner.Message}");
                _logger.LogWarning(ex, "Catalog import error for '{Title}'", cp.title);
                result.Skipped++;
                _db.ChangeTracker.Clear();
                RehydrateCaches(categories, attrs, valueCache);
            }
        }

        _db.ChangeTracker.AutoDetectChangesEnabled = true;
        await EnsureStoreInventoryRecordsAsync();
        Log($"Done: {result.Summary}");
        return result;
    }

    // ChangeTracker.Clear() detaches cached entities; reload their tracked instances so subsequent
    // FK assignments use attached references.
    private void RehydrateCaches(Dictionary<string, Category> categories,
        Dictionary<string, ProductAttribute> attrs,
        Dictionary<(int, string), ProductAttributeValue> valueCache)
    {
        foreach (var slug in categories.Keys.ToList())
            categories[slug] = _db.Categories.First(c => c.Id == categories[slug].Id);
        foreach (var slug in attrs.Keys.ToList())
            attrs[slug] = _db.ProductAttributes.First(a => a.Id == attrs[slug].Id);
        foreach (var key in valueCache.Keys.ToList())
            valueCache[key] = _db.ProductAttributeValues.First(v => v.Id == valueCache[key].Id);
    }

    private async Task EnsureStoreInventoryRecordsAsync()
    {
        var productIds = await _db.Products.Select(p => p.Id).ToListAsync();
        var storeIds = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();
        var existing = (await _db.StoreInventories.Select(si => new { si.ProductId, si.StoreId }).ToListAsync())
            .Select(e => (e.ProductId, e.StoreId)).ToHashSet();
        var toCreate = new List<StoreInventory>();
        foreach (var pid in productIds)
            foreach (var sid in storeIds)
                if (!existing.Contains((pid, sid)))
                    toCreate.Add(new StoreInventory { ProductId = pid, StoreId = sid, QuantityOnHand = 0, LastSyncedAt = DateTime.UtcNow });
        if (toCreate.Count > 0)
        {
            _db.StoreInventories.AddRange(toCreate);
            await _db.SaveChangesAsync();
        }
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, HashSet<string> used)
    {
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "product";
        var slug = baseSlug; var n = 1;
        while (used.Contains(slug) || await _db.Products.AnyAsync(p => p.Slug == slug))
            slug = $"{baseSlug}-{n++}";
        return slug;
    }

    private static string Slugify(string name) =>
        Regex.Replace((name ?? "").ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
