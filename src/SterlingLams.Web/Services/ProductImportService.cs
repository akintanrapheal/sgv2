using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.ERPNext;
using SterlingLams.Web.Services.ERPNext.ERPNextModels;

namespace SterlingLams.Web.Services;

/// <summary>
/// Imports the product catalog from ERPNext into the local PostgreSQL database.
/// Products are upserted (insert-or-update) by ErpNextItemCode.
/// </summary>
public interface IProductImportService
{
    Task<ProductImportResult> ImportAllFromERPNextAsync(IProgress<string>? progress = null);
}

public class ProductImportResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool Success => !Errors.Any();
    public string Summary => $"{Created} created, {Updated} updated, {Skipped} skipped" +
                             (Errors.Any() ? $", {Errors.Count} errors" : "");
}

public class ProductImportService : IProductImportService
{
    private readonly IERPNextService _erpNext;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ProductImportService> _logger;

    // Maps ERPNext item_group names (lowercased) to local category slugs
    private static readonly Dictionary<string, string> CategorySlugMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rings"]     = "rings",
        ["ring"]      = "rings",
        ["necklace"]  = "necklaces",
        ["necklaces"] = "necklaces",
        ["earring"]   = "earrings",
        ["earrings"]  = "earrings",
        ["bracelet"]  = "bracelets",
        ["bracelets"] = "bracelets",
        ["brooch"]    = "brooches",
        ["brooches"]  = "brooches",
        ["watch"]     = "watches",
        ["watches"]   = "watches",
        ["set"]       = "sets",
        ["sets"]      = "sets",
    };

    public ProductImportService(IERPNextService erpNext, ApplicationDbContext db, ILogger<ProductImportService> logger)
    {
        _erpNext = erpNext;
        _db = db;
        _logger = logger;
    }

    public async Task<ProductImportResult> ImportAllFromERPNextAsync(IProgress<string>? progress = null)
    {
        var result = new ProductImportResult();

        var localCategories = await _db.Categories.ToListAsync();
        var defaultCategory = localCategories.FirstOrDefault(c => c.Slug == "rings")
                           ?? localCategories.FirstOrDefault()
                           ?? throw new InvalidOperationException("No categories seeded. Run the seeder first.");

        var existingProducts = await _db.Products
            .ToDictionaryAsync(p => p.ErpNextItemCode, StringComparer.OrdinalIgnoreCase);

        int offset = 0;
        const int batchSize = 50;
        int totalFetched = 0;

        while (true)
        {
            progress?.Report($"Fetching items from ERPNext (offset {offset})…");

            List<ERPNextItem> batch;
            try
            {
                batch = await _erpNext.GetItemsAsync(offset, batchSize);
            }
            catch (Exception ex)
            {
                var msg = $"ERPNext fetch failed at offset {offset}: {ex.Message}";
                _logger.LogError(ex, msg);
                result.Errors.Add(msg);
                break;
            }

            if (!batch.Any()) break;

            totalFetched += batch.Count;
            progress?.Report($"Processing {batch.Count} items (total fetched: {totalFetched})…");

            foreach (var item in batch)
            {
                try
                {
                    var category = ResolveCategory(item.ItemGroup, localCategories) ?? defaultCategory;
                    var slug = Slugify(item.ItemName);

                    if (existingProducts.TryGetValue(item.ItemCode, out var existing))
                    {
                        existing.Name = item.ItemName;
                        existing.Price = item.StandardRate;
                        existing.Description = item.Description;
                        existing.IsActive = item.IsActive;
                        existing.CategoryId = category.Id;
                        existing.UpdatedAt = DateTime.UtcNow;
                        if (string.IsNullOrEmpty(existing.Slug))
                            existing.Slug = await UniqueSlugAsync(slug, existing.Id);

                        result.Updated++;
                    }
                    else
                    {
                        var uniqueSlug = await UniqueSlugAsync(slug, 0);
                        var product = new Product
                        {
                            ErpNextItemCode = item.ItemCode,
                            Name = item.ItemName,
                            Slug = uniqueSlug,
                            Price = item.StandardRate,
                            Description = item.Description,
                            IsActive = item.IsActive,
                            CategoryId = category.Id,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        _db.Products.Add(product);
                        existingProducts[item.ItemCode] = product;
                        result.Created++;
                    }
                }
                catch (Exception ex)
                {
                    var msg = $"Error importing item '{item.ItemName}' ({item.ItemCode}): {ex.Message}";
                    _logger.LogWarning(ex, msg);
                    result.Errors.Add(msg);
                    result.Skipped++;
                }
            }

            try
            {
                await _db.SaveChangesAsync();
                progress?.Report($"Saved batch. Running total: {result.Created} created, {result.Updated} updated.");
            }
            catch (Exception ex)
            {
                var msg = $"DB save failed for batch at offset {offset}: {ex.Message}";
                _logger.LogError(ex, msg);
                result.Errors.Add(msg);
            }

            if (batch.Count < batchSize) break;
            offset += batchSize;
        }

        progress?.Report($"Import complete. {result.Summary}");
        _logger.LogInformation("ERPNext product import complete: {Summary}", result.Summary);
        return result;
    }

    private Category? ResolveCategory(string? itemGroup, List<Category> categories)
    {
        if (string.IsNullOrWhiteSpace(itemGroup)) return null;

        foreach (var (key, slug) in CategorySlugMap)
        {
            if (itemGroup.Contains(key, StringComparison.OrdinalIgnoreCase))
                return categories.FirstOrDefault(c => c.Slug == slug);
        }

        return categories.FirstOrDefault(c =>
            itemGroup.Contains(c.Name, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains(itemGroup, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, int excludeId)
    {
        var slug = baseSlug;
        var counter = 1;
        while (await _db.Products.AnyAsync(p => p.Slug == slug && p.Id != excludeId))
        {
            slug = $"{baseSlug}-{counter++}";
        }
        return slug;
    }

    private static string Slugify(string name) =>
        System.Text.RegularExpressions.Regex
            .Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-")
            .Trim('-');
}