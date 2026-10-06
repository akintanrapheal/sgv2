using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SterlingLams.Web.Infrastructure;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// The old WooCommerce URLs are still in Google's index and still carry this site's ranking, so the rules
/// that resolve them are revenue-critical: ~514 of the 1,108 indexed product URLs were dead-ending (315 of
/// them bounced to /products?search=, which Google reads as a soft 404 and drops). These tests pin the
/// behaviour that replaced that, plus the invariants the generated slug map must keep.
/// </summary>
public class LegacyUrlRedirectTests
{
    [Theory]
    // Catalogue families keep the slug so the replacement page inherits the old one's ranking.
    [InlineData("/shop", "/products")]
    [InlineData("/shop/", "/products")]
    [InlineData("/product-category/bracelets", "/products?category=bracelets")]
    [InlineData("/product-category/bracelets/", "/products?category=bracelets")]
    // Old sitemap locations: Search Console still has these on file, so they must point at the live one.
    [InlineData("/sitemap_index.xml", "/sitemap.xml")]
    [InlineData("/wp-sitemap.xml", "/sitemap.xml")]
    // WordPress leftovers.
    [InlineData("/wp-login.php", "/")]
    [InlineData("/my-account", "/Account/Login")]
    [InlineData("/tag/gold", "/products")]
    public void MapLegacy_maps_known_old_urls(string path, string expected)
        => Assert.Equal(expected, LegacyUrlRedirectMiddleware.MapLegacy(path));

    [Theory]
    // Pages that already exist on the new site must not be remapped, or the redirect loops.
    [InlineData("/cart")]
    [InlineData("/checkout")]
    [InlineData("/products")]
    [InlineData("/products/some-live-product")]
    [InlineData("/")]
    [InlineData("")]
    public void MapLegacy_leaves_current_urls_alone(string path)
        => Assert.Null(LegacyUrlRedirectMiddleware.MapLegacy(path));

    [Fact]
    public void A_live_product_slug_keeps_its_slug()
    {
        // Not a retired slug → straight swap of the /shop prefix for /products.
        Assert.Equal("/products/a-slug-that-is-not-retired",
            LegacyUrlRedirectMiddleware.MapLegacy("/shop/a-slug-that-is-not-retired"));
    }

    [Fact]
    public void A_retired_product_slug_resolves_in_one_hop()
    {
        // Retired slugs are resolved by the middleware itself rather than being bounced to
        // /products/{old-slug} for the controller to redirect again — one 301, not two.
        var (oldSlug, target) = FirstMapEntry();
        Assert.Equal(target, LegacyUrlRedirectMiddleware.MapLegacy("/shop/" + oldSlug));
        Assert.Equal(target, LegacyUrlRedirectMiddleware.MapLegacy("/product/" + oldSlug));
    }

    [Fact]
    public void TryResolve_is_case_and_slash_insensitive()
    {
        var (oldSlug, target) = FirstMapEntry();
        Assert.True(LegacyProductSlugMap.TryResolve(oldSlug.ToUpperInvariant(), out var a));
        Assert.Equal(target, a);
        Assert.True(LegacyProductSlugMap.TryResolve("/" + oldSlug + "/", out var b));
        Assert.Equal(target, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-slug-that-is-not-retired")]
    public void TryResolve_returns_false_for_anything_it_does_not_know(string? slug)
    {
        Assert.False(LegacyProductSlugMap.TryResolve(slug, out var target));
        Assert.Equal("", target);
    }

    [Fact]
    public void Every_mapped_target_points_at_the_catalogue()
    {
        // A target outside /products would send indexed traffic somewhere unintended.
        var bad = Map().Where(kv => !kv.Value.StartsWith("/products", StringComparison.Ordinal)).ToList();
        Assert.True(bad.Count == 0, "targets outside /products: " + string.Join(", ", bad.Select(kv => $"{kv.Key} -> {kv.Value}")));
    }

    [Fact]
    public void No_slug_redirects_to_its_own_url()
    {
        // ProductsController consults this map only when the slug has no product, so a slug mapped to
        // /products/{itself} would 301 to the same URL forever.
        var selfish = Map().Where(kv => string.Equals(kv.Value, "/products/" + kv.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(selfish.Count == 0, "slugs mapped to themselves: " + string.Join(", ", selfish.Select(kv => kv.Key)));
    }

    [Fact]
    public void No_target_is_itself_a_retired_slug()
    {
        // Otherwise a hit chains 301 → 301 (and a cycle would never terminate). Each old URL should
        // reach its destination in one further hop.
        var map = Map();
        var chained = map
            .Where(kv => kv.Value.StartsWith("/products/", StringComparison.Ordinal))
            .Where(kv => map.ContainsKey(kv.Value["/products/".Length..]))
            .ToList();
        Assert.True(chained.Count == 0, "chained redirects: " + string.Join(", ", chained.Select(kv => $"{kv.Key} -> {kv.Value}")));
    }

    [Fact]
    public void The_map_actually_has_the_recovered_urls_in_it()
    {
        // Guards against a regeneration that silently produces an empty map.
        Assert.True(LegacyProductSlugMap.Count > 250, $"expected the recovered slugs to be present, found {LegacyProductSlugMap.Count}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────
    private static Dictionary<string, string> Map()
    {
        var f = typeof(LegacyProductSlugMap).GetField("Map", BindingFlags.NonPublic | BindingFlags.Static)!;
        return new Dictionary<string, string>((Dictionary<string, string>)f.GetValue(null)!, StringComparer.OrdinalIgnoreCase);
    }

    private static (string Slug, string Target) FirstMapEntry()
    {
        var first = Map().OrderBy(kv => kv.Key, StringComparer.Ordinal).First();
        return (first.Key, first.Value);
    }
}
