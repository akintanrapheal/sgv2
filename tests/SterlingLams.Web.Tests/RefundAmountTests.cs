using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// A refund must pay back what the customer actually TENDERED, never more. Anything that reduced the
/// bill without being tendered — a discount code, loyalty points, a gift-card balance — has to come
/// off the cash refund proportionally, because a full refund separately returns the points
/// (LoyaltyService.ReverseForOrderAsync) and the card balance (GiftCardService.ReverseForOrderAsync).
/// Refunding their cash value as well would compensate the customer twice.
///
/// This mirrors the arithmetic in OrdersController.RefundOrder / PosController.RefundProcess.
/// </summary>
public class RefundAmountTests
{
    /// <summary>The online rule: code discount + loyalty + gift card are all order-level.</summary>
    private static decimal OnlineRefund(Order order, decimal grossRefund)
    {
        var lineDiscountTotal = order.Items.Sum(i => i.DiscountAmount);
        var orderLevel = Math.Max(0, order.DiscountAmount - lineDiscountTotal)
                         + order.LoyaltyDiscount + order.GiftCardAmount;
        var totalNet = order.Items.Sum(i => i.UnitPrice * i.Quantity - i.DiscountAmount);
        return orderLevel > 0 && totalNet > 0
            ? Math.Max(0, grossRefund - Math.Round(orderLevel * grossRefund / totalNet, 2))
            : grossRefund;
    }

    /// <summary>The till rule: loyalty on its own field, falling back to the old folded-in form.</summary>
    private static decimal PosRefund(Order order, decimal grossRefund)
    {
        var lineDiscountTotal = order.Items.Sum(i => i.DiscountAmount);
        var orderLevel = order.LoyaltyDiscount > 0
            ? order.LoyaltyDiscount
            : Math.Max(0, order.DiscountAmount - lineDiscountTotal);
        var totalNet = order.Items.Sum(i => i.UnitPrice * i.Quantity - i.DiscountAmount);
        return orderLevel > 0 && totalNet > 0
            ? Math.Max(0, grossRefund - Math.Round(orderLevel * grossRefund / totalNet, 2))
            : grossRefund;
    }

    private static Order Order(decimal unit, int qty, decimal lineDiscount = 0,
        decimal discountAmount = 0, decimal loyalty = 0, decimal giftCard = 0) => new()
    {
        OrderNumber = "T-1",
        DiscountAmount = discountAmount,
        LoyaltyDiscount = loyalty,
        GiftCardAmount = giftCard,
        Items = { new OrderItem { ProductId = 1, Quantity = qty, UnitPrice = unit, DiscountAmount = lineDiscount } }
    };

    [Fact]
    public void An_order_with_no_discounts_refunds_the_full_line_value()
    {
        var o = Order(unit: 10_000m, qty: 2);
        Assert.Equal(20_000m, OnlineRefund(o, 20_000m));
    }

    [Fact]
    public void Loyalty_points_are_not_paid_back_as_cash()
    {
        // ₦20,000 of goods, ₦5,000 paid with points → the customer tendered ₦15,000.
        var o = Order(unit: 10_000m, qty: 2, loyalty: 5_000m);
        Assert.Equal(15_000m, OnlineRefund(o, 20_000m));
    }

    [Fact]
    public void A_gift_card_balance_is_not_paid_back_as_cash()
    {
        // ₦20,000 of goods, ₦8,000 drawn from a gift card → tendered ₦12,000.
        var o = Order(unit: 10_000m, qty: 2, giftCard: 8_000m);
        Assert.Equal(12_000m, OnlineRefund(o, 20_000m));
    }

    [Fact]
    public void A_code_discount_loyalty_and_a_gift_card_all_come_off_together()
    {
        // ₦20,000 of goods: ₦2,000 code + ₦3,000 points + ₦5,000 card → tendered ₦10,000.
        var o = Order(unit: 10_000m, qty: 2, discountAmount: 2_000m, loyalty: 3_000m, giftCard: 5_000m);
        Assert.Equal(10_000m, OnlineRefund(o, 20_000m));
    }

    [Fact]
    public void Refunding_half_an_order_pays_back_half_of_what_was_tendered()
    {
        var o = Order(unit: 10_000m, qty: 2, loyalty: 5_000m);   // tendered ₦15,000 for 2 units
        Assert.Equal(7_500m, OnlineRefund(o, 10_000m));          // one unit back → half
    }

    [Fact]
    public void An_order_paid_entirely_by_gift_card_refunds_no_cash_and_never_goes_negative()
    {
        var o = Order(unit: 10_000m, qty: 2, giftCard: 20_000m);
        Assert.Equal(0m, OnlineRefund(o, 20_000m));
    }

    [Fact]
    public void Till_sales_keep_loyalty_off_the_cash_refund_on_both_the_old_and_new_shape()
    {
        // New shape: loyalty on its own field, DiscountAmount holds only the line discount.
        var current = Order(unit: 10_000m, qty: 2, lineDiscount: 1_000m,
            discountAmount: 1_000m, loyalty: 4_000m);
        // Old shape: the same sale as recorded before the fix — loyalty folded into DiscountAmount.
        var legacy = Order(unit: 10_000m, qty: 2, lineDiscount: 1_000m,
            discountAmount: 5_000m, loyalty: 0m);

        // Goods net of the line discount = ₦19,000; ₦4,000 came off in points → ₦15,000 tendered.
        Assert.Equal(15_000m, PosRefund(current, 19_000m));
        Assert.Equal(15_000m, PosRefund(legacy, 19_000m));
    }
}
