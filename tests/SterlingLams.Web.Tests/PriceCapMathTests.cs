using SterlingLams.Web.Infrastructure;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>The "Cap discounts at 15%" tool: anything discounted MORE than 15% is raised to exactly 15%
/// off; a discount of 15% or less (and full-price items) is left alone; the rounded price never ends up
/// MORE than 15% off; and capping is idempotent (re-running changes nothing).</summary>
public class PriceCapMathTests
{
    [Theory]
    [InlineData(10000, 6000, true)]   // 40% off → cap
    [InlineData(10000, 8499, true)]   // 15.01% off → cap
    [InlineData(10000, 8500, false)]  // exactly 15% off → leave
    [InlineData(10000, 9000, false)]  // 10% off → leave
    [InlineData(10000, 10000, false)] // no discount → leave
    [InlineData(10000, 0, false)]     // ₦0 sale price = NOT on sale → leave (don't create a new discount)
    [InlineData(0, 0, false)]         // no regular price → leave
    public void ShouldCap_only_when_discount_exceeds_15_percent(decimal regular, decimal value, bool expected)
        => Assert.Equal(expected, PriceCapMath.ShouldCap(regular, value));

    [Theory]
    [InlineData(10000, 8500)]   // clean
    [InlineData(4999, 4250)]    // 4249.15 → rounded UP to 4250
    [InlineData(1999, 1700)]    // 1699.15 → 1700
    [InlineData(12000, 10200)]  // exact
    public void NewPrice_is_ceil_of_85_percent(decimal regular, decimal expected)
        => Assert.Equal(expected, PriceCapMath.NewPrice(regular));

    [Theory]
    [InlineData(10000)]
    [InlineData(4999)]
    [InlineData(1999)]
    [InlineData(37550)]
    [InlineData(950)]
    public void Capped_price_is_never_more_than_15_percent_off(decimal regular)
    {
        var capped = PriceCapMath.NewPrice(regular);
        var discountPct = (regular - capped) / regular * 100m;
        Assert.True(discountPct <= 15m, $"₦{regular} capped to ₦{capped} = {discountPct:0.##}% off, which exceeds 15%");
    }

    [Theory]
    [InlineData(10000, 6000)]
    [InlineData(4999, 1000)]
    [InlineData(37550, 20000)]
    public void Capping_is_idempotent(decimal regular, decimal deepDiscount)
    {
        Assert.True(PriceCapMath.ShouldCap(regular, deepDiscount));   // first run caps it
        var capped = PriceCapMath.NewPrice(regular);
        Assert.False(PriceCapMath.ShouldCap(regular, capped));        // second run leaves it alone
    }
}
