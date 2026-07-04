using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Discount code evaluation: correct amount, and the guards (min order, inactive,
/// unknown, usage limit).</summary>
public class DiscountServiceTests
{
    private static CartViewModel Cart(decimal unit, int qty) => new()
    {
        Items = { new CartItemViewModel { ProductId = 1, ProductName = "X", UnitPrice = unit, Quantity = qty } }
    };

    private static DiscountCode Seed(TestDb t, Action<DiscountCode>? tweak = null)
    {
        var d = new DiscountCode
        {
            Code = "SAVE10", Type = DiscountType.Percentage, Value = 10,
            IsActive = true, Scope = DiscountScope.EntireOrder
        };
        tweak?.Invoke(d);
        t.Db.DiscountCodes.Add(d);
        t.Db.SaveChanges();
        return d;
    }

    [Fact]
    public async Task Valid_percentage_code_returns_correct_amount()
    {
        using var t = new TestDb();
        Seed(t);

        // Code lookup is case-insensitive (normalised to upper).
        var r = await new DiscountService(t.Db).EvaluateAsync("save10", Cart(1000, 2), userId: "u1");

        Assert.True(r.Success, r.Error);
        Assert.Equal(200m, r.Amount); // 10% of ₦2,000
    }

    [Fact]
    public async Task Fixed_amount_is_capped_at_the_eligible_subtotal()
    {
        using var t = new TestDb();
        Seed(t, d => { d.Type = DiscountType.FixedAmount; d.Value = 5000; });

        var r = await new DiscountService(t.Db).EvaluateAsync("SAVE10", Cart(1000, 2), userId: "u1");

        Assert.True(r.Success, r.Error);
        Assert.Equal(2000m, r.Amount); // capped at the ₦2,000 subtotal, not ₦5,000
    }

    [Fact]
    public async Task Below_minimum_order_is_rejected()
    {
        using var t = new TestDb();
        Seed(t, d => d.MinimumOrderAmount = 5000);

        var r = await new DiscountService(t.Db).EvaluateAsync("SAVE10", Cart(1000, 2), userId: "u1");

        Assert.False(r.Success);
    }

    [Fact]
    public async Task Inactive_code_is_rejected()
    {
        using var t = new TestDb();
        Seed(t, d => d.IsActive = false);

        var r = await new DiscountService(t.Db).EvaluateAsync("SAVE10", Cart(1000, 2), userId: "u1");

        Assert.False(r.Success);
    }

    [Fact]
    public async Task Used_up_code_is_rejected()
    {
        using var t = new TestDb();
        Seed(t, d => { d.MaxUses = 5; d.UsedCount = 5; });

        var r = await new DiscountService(t.Db).EvaluateAsync("SAVE10", Cart(1000, 2), userId: "u1");

        Assert.False(r.Success);
    }

    [Fact]
    public async Task Unknown_code_is_rejected()
    {
        using var t = new TestDb();

        var r = await new DiscountService(t.Db).EvaluateAsync("NOPE", Cart(1000, 2), userId: "u1");

        Assert.False(r.Success);
    }
}
