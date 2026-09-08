using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Fills the min–max variant price range on a page of product cards in ONE grouped query (no per-card
/// N+1) — the same shape as how card ratings are attached. Reuses <see cref="VariantPricing"/> so a
/// card's range matches the product detail page exactly: the effective (sale-aware) prices of ALL active
/// variants. The range is deliberately stock-independent so a multi-priced product keeps showing its
/// full "₦min – ₦max" even when some tiers are temporarily sold out (the storefront still flags the
/// product "Sold Out" only when nothing is in stock, and the detail page disables the picked-out option).
///
/// Only variable products are queried. A product whose variants all inherit the base price resolves to
/// min == max, so <see cref="ProductCardViewModel.HasPriceRange"/> stays false and the card shows a
/// single price (with its normal sale strike-through) — the "no variant price set → fall back to base
/// price and sale" behaviour.
/// </summary>
public static class ProductCardPricing
{
    public static async Task ApplyVariantPriceRangesAsync(IReadOnlyList<ProductCardViewModel> cards, ApplicationDbContext db)
    {
        var ids = cards.Where(c => c.HasVariants).Select(c => c.Id).ToList();
        if (ids.Count == 0) return;

        var rows = await db.ProductVariants
            .Where(v => ids.Contains(v.ProductId) && v.IsActive)
            .Select(v => new
            {
                v.ProductId,
                v.Price,
                v.SalePrice
            })
            .ToListAsync();

        var byProduct = rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var card in cards)
        {
            if (!byProduct.TryGetValue(card.Id, out var variants) || variants.Count == 0) continue;

            // A lightweight product carrying the base price + sale window, so VariantPricing applies the
            // identical rule as the detail page (a null variant price falls back to base price + sale).
            var product = new Product
            {
                Price = card.Price,
                SalePrice = card.SalePrice,
                SaleStartsAt = card.SaleStartsAt,
                SaleEndsAt = card.SaleEndsAt,
            };
            // Range over ALL active variants (stock-independent) — see the class summary.
            var effective = variants
                .Select(r => VariantPricing.EffectivePrice(product, new ProductVariant { Price = r.Price, SalePrice = r.SalePrice }))
                .ToList();
            if (effective.Count == 0) continue;

            card.MinVariantPrice = effective.Min();
            card.MaxVariantPrice = effective.Max();
        }
    }
}
