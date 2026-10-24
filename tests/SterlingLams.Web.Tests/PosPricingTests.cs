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
                    ? x.Variants.Where(v => v.IsActive).Min(v => v.PosPrice ?? v.Price ?? x.PosPrice ?? x.Price)
                    : (x.PosPrice ?? (x.SalePrice != null && x.SalePrice > 0 && x.SalePrice < x.Price
                            && (x.SaleStartsAt == null || x.SaleStartsAt <= DateTime.UtcNow)
                            && (x.SaleEndsAt == null || x.SaleEndsAt >= DateTime.UtcNow)
                        ? x.SalePrice.Value : x.Price))))
            .SingleAsync();

    // Mirrors the EPOS variant-picker price projection in PosController — must equal PosCharge.
    private static async Task<decimal?> VariantPickerPriceAsync(TestDb t, int variantId) =>
        await t.Db.ProductVariants.Where(v => v.Id == variantId)
            .Select(v => (decimal?)(v.PosPrice ?? v.Price ?? v.Product.PosPrice ?? v.Product.Price))
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
    public async Task Variable_variant_with_no_own_price_charges_regular_price_not_website_sale()
    {
        // Regression: a product converted simple→variable leaves the generated variant with no price of its
        // own (Price/PosPrice null) while the product still carries a website SalePrice. The till CARD showed
        // the regular price (₦6,950) but PosCharge used to fall through to p.EffectivePrice (the ₦4,950 sale),
        // so the item rang up at a different price than shown. Charge must equal the card: the regular price.
        using var t = new TestDb();
        var cat = SeedCategory(t, "Rings");
        var p = new Product
        {
            Name = "Gold Stone Ring", Slug = "gold-stone-ring", ProductType = "variable",
            IsActive = true, CategoryId = cat.Id,
            Price = 6950m, SalePrice = 4950m,     // website on sale at ₦4,950
            PosPrice = null,
            Variants = { new ProductVariant { Name = "8", IsActive = true } } // no own price (just converted)
        };
        t.Db.Products.Add(p);
        await t.Db.SaveChangesAsync();

        var variant = p.Variants.Single();
        Assert.Equal(6950m, SterlingLams.Web.Controllers.PosController.PosCharge(p, variant)); // regular, not sale
        Assert.Equal(await CardPriceAsync(t, p.Id), SterlingLams.Web.Controllers.PosController.PosCharge(p, variant)); // card == charge
    }

    [Fact]
    public async Task Variant_picker_shows_each_variant_charged_price_not_the_cheapest_variant()
    {
        // Regression (Plain Gold Band Ring): every variant is the product's ₦11,950 except ONE odd variant
        // with a ₦7,500 EPOS price. The picker projected `v.PosPrice ?? v.Price` → null for the normal
        // variants, so the till showed the card's "from ₦7,500" for EVERY variant while charging ₦11,950 —
        // a customer saw ₦7,500 but was billed ₦11,950. The picker price must equal what each variant charges.
        using var t = new TestDb();
        var cat = SeedCategory(t, "Rings");
        var p = new Product
        {
            Name = "Plain Gold Band Ring", Slug = "plain-gold-band-ring", ProductType = "variable",
            IsActive = true, CategoryId = cat.Id, Price = 11950m, PosPrice = null,
            Variants =
            {
                new ProductVariant { Name = "9 / Gold", IsActive = true },                   // no own price
                new ProductVariant { Name = "5-5 / Silver", IsActive = true, PosPrice = 7500m } // the odd one
            }
        };
        t.Db.Products.Add(p);
        await t.Db.SaveChangesAsync();

        var normal = p.Variants.Single(v => v.Name == "9 / Gold");
        var odd = p.Variants.Single(v => v.Name.StartsWith("5-5"));

        Assert.Equal(11950m, await VariantPickerPriceAsync(t, normal.Id));   // shows its own price, not ₦7,500
        Assert.Equal(7500m, await VariantPickerPriceAsync(t, odd.Id));
        // Display == charge for every variant.
        Assert.Equal(SterlingLams.Web.Controllers.PosController.PosCharge(p, normal), await VariantPickerPriceAsync(t, normal.Id));
        Assert.Equal(SterlingLams.Web.Controllers.PosController.PosCharge(p, odd), await VariantPickerPriceAsync(t, odd.Id));
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
