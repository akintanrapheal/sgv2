using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>The EPOS product grid must show the EPOS price, never the website sale price. For a variable
/// product the EPOS price lives on the variants, so the card shows the representative (lowest) variant EPOS
/// price — matching the variant picker and what's charged when added to the sale. Regression for the
/// Sparkle Clutches card showing the website sale price (₦29,950) instead of the EPOS price (₦39,950).</summary>
public class PosPricingTests
{
    // Mirrors the EPOS product-grid price projection in PosController.
    private static async Task<decimal?> CardPriceAsync(TestDb t, int productId) =>
        await t.Db.Products.Where(x => x.IsActive && !x.HiddenFromPos && x.Id == productId)
            .Select(x => (decimal?)(
                (x.ProductType == "variable" && x.Variants.Any(v => v.IsActive))
                    ? x.Variants.Where(v => v.IsActive).Min(v => v.PosPrice ?? v.Price ?? x.Price)
                    : (x.PosPrice ?? (x.SalePrice != null && x.SalePrice > 0 && x.SalePrice < x.Price
                            && (x.SaleStartsAt == null || x.SaleStartsAt <= DateTime.UtcNow)
                            && (x.SaleEndsAt == null || x.SaleEndsAt >= DateTime.UtcNow)
                        ? x.SalePrice.Value : x.Price))))
            .SingleAsync();

    private static Category SeedCategory(TestDb t, string name)
    {
        var c = new Category { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), IsActive = true };
        t.Db.Categories.Add(c);
        t.Db.SaveChanges();
        return c;
    }

    [Fact]
    public async Task Variable_product_card_shows_variant_epos_price_not_website_sale_price()
    {
        using var t = new TestDb();
        var cat = SeedCategory(t, "Stoned Clutches");
        var p = new Product
        {
            Name = "Sparkle Clutches", Slug = "sparkle-clutches", ProductType = "variable",
            IsActive = true, CategoryId = cat.Id,
            Price = 44950m, SalePrice = 29950m,   // website: on sale at ₦29,950
            PosPrice = null,                       // EPOS price lives on the variants
            Variants =
            {
                new ProductVariant { Name = "Black", IsActive = true, Price = 44950m, PosPrice = 39950m },
                new ProductVariant { Name = "Blue",  IsActive = true, Price = 44950m, PosPrice = 39950m },
            }
        };
        t.Db.Products.Add(p);
        await t.Db.SaveChangesAsync();

        Assert.Equal(39950m, await CardPriceAsync(t, p.Id));   // EPOS price, NOT the website sale ₦29,950
    }

    [Fact]
    public async Task Simple_product_card_uses_pos_price_when_set()
    {
        using var t = new TestDb();
        var cat = SeedCategory(t, "Rings");
        var p = new Product
        {
            Name = "Plain Ring", Slug = "plain-ring", ProductType = "simple", IsActive = true,
            CategoryId = cat.Id, Price = 10000m, SalePrice = 6000m, PosPrice = 8000m,
        };
        t.Db.Products.Add(p);
        await t.Db.SaveChangesAsync();

        Assert.Equal(8000m, await CardPriceAsync(t, p.Id));    // PosPrice wins over the website sale price
    }
}
