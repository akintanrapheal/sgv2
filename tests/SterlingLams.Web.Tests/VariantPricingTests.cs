using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// Per-variant pricing (<see cref="VariantPricing"/>): each variant can carry its own regular AND sale
/// price. Rules: regular = variant.Price ?? base; a variant with its OWN price is on sale only when it
/// has its OWN sale price; a variant with a null price inherits the base price AND the base sale; the
/// schedule is always the PRODUCT's sale window. Prices are absolute (no adjustment math).
/// </summary>
public class VariantPricingTests
{
    private static Product Product(decimal price, decimal? sale = null, DateTime? start = null, DateTime? end = null)
        => new() { Name = "p", Price = price, SalePrice = sale, SaleStartsAt = start, SaleEndsAt = end };

    private static ProductVariant Variant(decimal? price = null, decimal? sale = null)
        => new() { Name = "v", Price = price, SalePrice = sale };

    [Fact]
    public void Custom_priced_variant_without_sale_is_not_on_sale()
    {
        var p = Product(1000m, sale: 700m); // base is on sale, but the variant sets its own price
        var v = Variant(price: 2000m);
        Assert.False(VariantPricing.IsOnSale(p, v));
        Assert.Equal(2000m, VariantPricing.EffectivePrice(p, v));   // NOT the base sale
        Assert.Equal(2000m, VariantPricing.RegularPrice(p, v));
    }

    [Fact]
    public void Custom_priced_variant_with_own_sale_is_on_sale()
    {
        var p = Product(1000m);
        var v = Variant(price: 2000m, sale: 1500m);
        Assert.True(VariantPricing.IsOnSale(p, v));
        Assert.Equal(1500m, VariantPricing.EffectivePrice(p, v));
        Assert.Equal(2000m, VariantPricing.RegularPrice(p, v));
    }

    [Fact]
    public void Variant_sale_not_below_its_regular_is_ignored()
    {
        var p = Product(1000m);
        Assert.False(VariantPricing.IsOnSale(p, Variant(price: 2000m, sale: 2000m)));
        Assert.False(VariantPricing.IsOnSale(p, Variant(price: 2000m, sale: 2500m)));
        Assert.Equal(2000m, VariantPricing.EffectivePrice(p, Variant(price: 2000m, sale: 2500m)));
    }

    [Fact]
    public void Null_priced_variant_inherits_base_price_and_base_sale()
    {
        var p = Product(1000m, sale: 800m);
        var v = Variant(price: null, sale: null);
        Assert.True(VariantPricing.IsOnSale(p, v));
        Assert.Equal(800m, VariantPricing.EffectivePrice(p, v));    // inherits base sale
        Assert.Equal(1000m, VariantPricing.RegularPrice(p, v));
    }

    [Fact]
    public void Null_priced_variant_with_base_not_on_sale_uses_base_price()
    {
        var p = Product(1000m);
        var v = Variant(price: null, sale: null);
        Assert.False(VariantPricing.IsOnSale(p, v));
        Assert.Equal(1000m, VariantPricing.EffectivePrice(p, v));
    }

    [Fact]
    public void Variant_sale_respects_the_product_window()
    {
        var future = Product(1000m, start: DateTime.UtcNow.AddDays(1));         // window not open yet
        var live   = Product(1000m, start: DateTime.UtcNow.AddHours(-1), end: DateTime.UtcNow.AddHours(1));

        Assert.False(VariantPricing.IsOnSale(future, Variant(price: 2000m, sale: 1500m)));
        Assert.Equal(2000m, VariantPricing.EffectivePrice(future, Variant(price: 2000m, sale: 1500m)));

        Assert.True(VariantPricing.IsOnSale(live, Variant(price: 2000m, sale: 1500m)));
        Assert.Equal(1500m, VariantPricing.EffectivePrice(live, Variant(price: 2000m, sale: 1500m)));
    }

    [Fact]
    public void Null_variant_matches_the_base_product()
    {
        var onSale = Product(1000m, sale: 800m);
        Assert.True(VariantPricing.IsOnSale(onSale, null));
        Assert.Equal(onSale.EffectivePrice, VariantPricing.EffectivePrice(onSale, null));

        var notOnSale = Product(1000m);
        Assert.False(VariantPricing.IsOnSale(notOnSale, null));
        Assert.Equal(1000m, VariantPricing.EffectivePrice(notOnSale, null));
    }
}
