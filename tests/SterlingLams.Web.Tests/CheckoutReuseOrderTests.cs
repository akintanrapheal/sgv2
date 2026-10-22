using System;
using System.Linq;
using System.Threading.Tasks;
using SterlingLams.Web.Controllers;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>When a customer's payment fails/is abandoned and they retry, checkout reuses their existing
/// still-pending order for the same items instead of creating a duplicate row. These cover the matcher
/// that decides what is reusable.</summary>
public class CheckoutReuseOrderTests
{
    private static CartViewModel Cart(params (int pid, int qty)[] lines)
    {
        var c = new CartViewModel();
        foreach (var (pid, qty) in lines)
            c.Items.Add(new CartItemViewModel { ProductId = pid, Quantity = qty, UnitPrice = 1000m, ProductName = $"P{pid}" });
        return c;
    }

    private static Order PendingOrder(TestDb t, string userId, params (int pid, int qty)[] lines)
    {
        var o = new Order
        {
            OrderNumber = "SGW-" + Guid.NewGuid().ToString("N")[..6], UserId = userId,
            Channel = OrderChannel.Online, FulfillmentType = FulfillmentType.Delivery,
            Status = OrderStatus.Pending, IsPaid = false, CreatedAt = DateTime.UtcNow,
            Currency = "NGN", Subtotal = 1000m, Total = 1000m,
        };
        foreach (var (pid, qty) in lines)
            o.Items.Add(new OrderItem { ProductId = pid, ProductName = $"P{pid}", Quantity = qty, UnitPrice = 1000m });
        t.Db.Orders.Add(o);
        return o;
    }

    [Fact]
    public async Task Reuses_a_pending_order_with_the_same_items()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var a = t.SeedProduct(); var b = t.SeedProduct();
        var existing = PendingOrder(t, u.Id, (a.Id, 2), (b.Id, 1));
        await t.Db.SaveChangesAsync();

        var match = await CheckoutController.FindReusablePendingOrderAsync(t.Db, u.Id, Cart((b.Id, 1), (a.Id, 2)));

        Assert.NotNull(match);
        Assert.Equal(existing.Id, match!.Id);   // order of lines doesn't matter
    }

    [Fact]
    public async Task Does_not_reuse_when_items_or_quantities_differ()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var a = t.SeedProduct(); var b = t.SeedProduct();
        PendingOrder(t, u.Id, (a.Id, 2));
        await t.Db.SaveChangesAsync();

        Assert.Null(await CheckoutController.FindReusablePendingOrderAsync(t.Db, u.Id, Cart((a.Id, 3))));              // qty differs
        Assert.Null(await CheckoutController.FindReusablePendingOrderAsync(t.Db, u.Id, Cart((b.Id, 2))));             // product differs
        Assert.Null(await CheckoutController.FindReusablePendingOrderAsync(t.Db, u.Id, Cart((a.Id, 2), (b.Id, 1))));  // extra line
    }

    [Fact]
    public async Task Excludes_paid_cancelled_staff_and_voided_orders()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var a = t.SeedProduct();

        var paid = PendingOrder(t, u.Id, (a.Id, 1)); paid.IsPaid = true; paid.Status = OrderStatus.Confirmed;
        var cancelled = PendingOrder(t, u.Id, (a.Id, 1)); cancelled.Status = OrderStatus.Cancelled;
        var staff = PendingOrder(t, u.Id, (a.Id, 1)); staff.PlacedByStaffUserId = "staff-1";
        var voided = PendingOrder(t, u.Id, (a.Id, 1)); voided.VoidedAt = DateTime.UtcNow;
        await t.Db.SaveChangesAsync();

        Assert.Null(await CheckoutController.FindReusablePendingOrderAsync(t.Db, u.Id, Cart((a.Id, 1))));
    }

    [Fact]
    public async Task Excludes_orders_older_than_24h_and_other_customers()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var other = t.SeedUser();
        var a = t.SeedProduct();

        var old = PendingOrder(t, u.Id, (a.Id, 1)); old.CreatedAt = DateTime.UtcNow.AddHours(-25);
        PendingOrder(t, other.Id, (a.Id, 1));   // same items, different customer
        await t.Db.SaveChangesAsync();

        Assert.Null(await CheckoutController.FindReusablePendingOrderAsync(t.Db, u.Id, Cart((a.Id, 1))));
    }

    [Fact]
    public async Task Returns_the_most_recent_matching_order()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var a = t.SeedProduct();
        var older = PendingOrder(t, u.Id, (a.Id, 1)); older.CreatedAt = DateTime.UtcNow.AddMinutes(-30);
        var newer = PendingOrder(t, u.Id, (a.Id, 1)); newer.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
        await t.Db.SaveChangesAsync();

        var match = await CheckoutController.FindReusablePendingOrderAsync(t.Db, u.Id, Cart((a.Id, 1)));
        Assert.Equal(newer.Id, match!.Id);
    }
}
