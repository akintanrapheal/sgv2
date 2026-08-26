namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// Single source of truth for what a product/variant combination actually costs on the storefront and
/// at checkout. Mirrors <see cref="Product.IsOnSale"/>/<see cref="Product.EffectivePrice"/> but adds
/// per-variant regular AND sale prices. Prices are absolute (no adjustment math — see the variant-pricing
/// note). The sale schedule is the PRODUCT's window; variants differ only in the amounts.
///
/// Rules (a null variant behaves exactly like a variant that inherits both prices from the product):
///   • Regular  = variant.Price ?? product.Price
///   • Sale     = variant.SalePrice, OR the product's SalePrice only when the variant has no own Price
///                (a variant with its own Price is on sale ONLY if it has its own SalePrice).
///   • On sale  = a sale price is set, &gt; 0, below the regular price, AND inside the product window.
/// </summary>
public static class VariantPricing
{
    /// <summary>The regular (pre-sale) price for this variant, falling back to the product price.</summary>
    public static decimal RegularPrice(Product product, ProductVariant? variant)
        => variant?.Price ?? product.Price;

    /// <summary>The active sale price, or null when this combination is not on sale.</summary>
    public static decimal? SalePriceOrNull(Product product, ProductVariant? variant)
    {
        // A variant's own sale price always applies; the product's sale price is inherited ONLY when the
        // variant has no price of its own (so a custom-priced variant with a blank sale price isn't on sale).
        var candidate = variant?.SalePrice ?? (variant?.Price == null ? product.SalePrice : null);
        if (candidate is not decimal sale || sale <= 0m) return null;

        var regular = RegularPrice(product, variant);
        if (sale >= regular) return null;

        var now = DateTime.UtcNow;
        var withinWindow = (product.SaleStartsAt == null || product.SaleStartsAt <= now)
                        && (product.SaleEndsAt == null || product.SaleEndsAt >= now);
        return withinWindow ? sale : null;
    }

    /// <summary>True when a valid sale price is in effect for this combination.</summary>
    public static bool IsOnSale(Product product, ProductVariant? variant)
        => SalePriceOrNull(product, variant) != null;

    /// <summary>The price actually charged: the sale price when on sale, otherwise the regular price.</summary>
    public static decimal EffectivePrice(Product product, ProductVariant? variant)
        => SalePriceOrNull(product, variant) ?? RegularPrice(product, variant);
}
