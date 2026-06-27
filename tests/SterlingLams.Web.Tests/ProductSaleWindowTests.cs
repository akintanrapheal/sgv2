using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// The scheduled-sale window on <see cref="Product"/>: the sale price only counts as "on sale"
/// inside [SaleStartsAt, SaleEndsAt] (UTC); null on either side = open-ended.
/// </summary>
public class ProductSaleWindowTests
{
    private static Product Make(decimal price, decimal? sale, DateTime? start = null, DateTime? end = null)
        => new() { Name = "x", Price = price, SalePrice = sale, SaleStartsAt = start, SaleEndsAt = end };

    [Fact]
    public void Not_on_sale_when_no_sale_price()
    {
        var p = Make(1000m, null);
        Assert.False(p.IsOnSale);
        Assert.Equal(1000m, p.EffectivePrice);
    }

    [Fact]
    public void Not_on_sale_when_sale_price_not_below_price()
    {
        Assert.False(Make(1000m, 1000m).IsOnSale);
        Assert.False(Make(1000m, 1200m).IsOnSale);
    }

    [Fact]
    public void On_sale_when_below_price_and_no_window()
    {
        var p = Make(1000m, 800m);
        Assert.True(p.IsOnSale);
        Assert.Equal(800m, p.EffectivePrice);
    }

    [Fact]
    public void Not_on_sale_before_the_window_opens()
    {
        var p = Make(1000m, 800m, start: DateTime.UtcNow.AddDays(1));
        Assert.False(p.IsOnSale);
        Assert.Equal(1000m, p.EffectivePrice); // still full price
    }

    [Fact]
    public void Not_on_sale_after_the_window_closes()
    {
        var p = Make(1000m, 800m, end: DateTime.UtcNow.AddDays(-1));
        Assert.False(p.IsOnSale);
        Assert.Equal(1000m, p.EffectivePrice);
    }

    [Fact]
    public void On_sale_inside_the_window()
    {
        var p = Make(1000m, 800m, start: DateTime.UtcNow.AddHours(-1), end: DateTime.UtcNow.AddHours(1));
        Assert.True(p.IsOnSale);
        Assert.Equal(800m, p.EffectivePrice);
    }
}
