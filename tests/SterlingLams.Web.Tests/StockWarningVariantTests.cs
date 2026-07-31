using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// Stock Warnings and the Reorder Worksheet show one row per product per branch, taking that
/// branch's Min/Max from the product-level (pool) row — but the QUANTITY has to be the product's
/// whole holding there, pool row plus every variant row. Reading the pool row alone reported every
/// product with options as 0 on hand, so they sat in the warnings list permanently and the worksheet
/// suggested reordering their full maximum while the variants were fully stocked.
///
/// These run the same query shape the reports use, which also proves the correlated sub-queries
/// translate to SQL rather than blowing up at runtime.
/// </summary>
public class StockWarningVariantTests
{
    /// <summary>A product with two options, stocked only on the variant rows, plus an empty pool row
    /// (which is what "Create Missing Records" and the older stock-take path leave behind).</summary>
    private static async Task<Product> SeedVariantProductAsync(TestDb t, int storeId, int perVariant)
    {
        var p = t.SeedProduct();
        p.ProductType = "variable";
        p.LowStockThreshold = 3;
        var gold = new ProductVariant { ProductId = p.Id, Name = "Gold", IsActive = true };
        var silver = new ProductVariant { ProductId = p.Id, Name = "Silver", IsActive = true };
        t.Db.ProductVariants.AddRange(gold, silver);
        await t.Db.SaveChangesAsync();

        t.Db.StoreInventories.AddRange(
            new StoreInventory { ProductId = p.Id, StoreId = storeId, ProductVariantId = null, QuantityOnHand = 0 },
            new StoreInventory { ProductId = p.Id, StoreId = storeId, ProductVariantId = gold.Id, QuantityOnHand = perVariant },
            new StoreInventory { ProductId = p.Id, StoreId = storeId, ProductVariantId = silver.Id, QuantityOnHand = perVariant });
        await t.Db.SaveChangesAsync();
        return p;
    }

    /// <summary>The Stock Warnings predicate.</summary>
    private static IQueryable<StoreInventory> LowStock(TestDb t) =>
        t.Db.StoreInventories
            .Where(si => si.Product.IsActive && si.ProductVariantId == null && si.Store.IsActive
                      && (si.MinStock ?? si.Product.LowStockThreshold) > 0
                      && t.Db.StoreInventories
                             .Where(x => x.ProductId == si.ProductId && x.StoreId == si.StoreId)
                             .Sum(x => x.QuantityOnHand)
                         <= (si.MinStock ?? si.Product.LowStockThreshold));

    [Fact]
    public async Task A_well_stocked_product_with_options_is_not_reported_as_low()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Abuja", "Abuja", "Gwarimpa");
        var p = await SeedVariantProductAsync(t, store.Id, perVariant: 10);   // 20 on hand, threshold 3

        var flagged = await LowStock(t).Select(si => si.ProductId).ToListAsync();

        Assert.DoesNotContain(p.Id, flagged);
    }

    [Fact]
    public async Task A_product_with_options_that_really_is_low_still_gets_reported()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Abuja", "Abuja", "Gwarimpa");
        var p = await SeedVariantProductAsync(t, store.Id, perVariant: 1);    // 2 on hand, threshold 3

        var flagged = await LowStock(t).Select(si => si.ProductId).ToListAsync();

        Assert.Contains(p.Id, flagged);
    }

    [Fact]
    public async Task A_simple_product_behaves_exactly_as_before()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Allen", "Lagos", "Ikeja");

        var low = t.SeedProduct();
        low.LowStockThreshold = 3;
        var fine = t.SeedProduct();
        fine.LowStockThreshold = 3;
        await t.Db.SaveChangesAsync();
        t.SetStock(low.Id, store.Id, onHand: 2);
        t.SetStock(fine.Id, store.Id, onHand: 9);

        var flagged = await LowStock(t).Select(si => si.ProductId).ToListAsync();

        Assert.Contains(low.Id, flagged);
        Assert.DoesNotContain(fine.Id, flagged);
    }

    [Fact]
    public async Task The_quantity_shown_is_the_whole_branch_holding_not_the_empty_pool_row()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Ikota", "Lagos", "Ajah");
        var p = await SeedVariantProductAsync(t, store.Id, perVariant: 4);    // 8 across two options

        var shown = await t.Db.StoreInventories
            .Where(si => si.ProductId == p.Id && si.StoreId == store.Id && si.ProductVariantId == null)
            .Select(si => t.Db.StoreInventories
                .Where(x => x.ProductId == si.ProductId && x.StoreId == si.StoreId)
                .Sum(x => x.QuantityOnHand))
            .SingleAsync();

        Assert.Equal(8, shown);   // not the pool row's 0
    }
}
