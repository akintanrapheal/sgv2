using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// The End of Day day-rule: a counter sale counts on the day it was paid, a website order on the day it
/// was packed. Run against real SQLite (not LINQ-to-objects) so the expression is proven to translate to
/// SQL — the query is built straight into EF, so a translation failure would only surface in production.
/// </summary>
public class EodDayRuleTests
{
    private static readonly DateTime Mon = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tue = Mon.AddDays(1);
    private static readonly DateTime Wed = Mon.AddDays(2);

    private static Order Add(TestDb t, ApplicationUser u, OrderChannel channel, bool paid,
        DateTime created, DateTime? paidAt = null, DateTime? packedAt = null, DateTime? pickupReadyAt = null)
    {
        var o = new Order
        {
            OrderNumber = "T-" + Guid.NewGuid().ToString("N")[..10],
            UserId = u.Id,
            Channel = channel,
            Status = OrderStatus.Confirmed,
            Currency = "NGN",
            Total = 1000m,
            IsPaid = paid,
            CreatedAt = created,
            PaidAt = paidAt,
            PackedAt = packedAt,
            PickupReadyEmailedAt = pickupReadyAt
        };
        t.Db.Orders.Add(o);
        t.Db.SaveChanges();
        return o;
    }

    /// <summary>Order numbers the EOD for Tuesday picks up — executed as SQL.</summary>
    private static List<string> OnTuesday(TestDb t) => t.Db.Orders
        .Where(EodDayRule.InWindow(Tue, Wed))
        .Select(o => o.OrderNumber)
        .ToList();

    [Fact]
    public void A_website_order_paid_monday_but_packed_tuesday_counts_on_tuesday()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var o = Add(t, u, OrderChannel.Online, paid: true,
            created: Mon.AddHours(9), paidAt: Mon.AddHours(10), packedAt: Tue.AddHours(11));

        Assert.Contains(o.OrderNumber, OnTuesday(t));
        // ...and is no longer counted on the day it was paid.
        Assert.DoesNotContain(o.OrderNumber,
            t.Db.Orders.Where(EodDayRule.InWindow(Mon, Tue)).Select(x => x.OrderNumber).ToList());
    }

    [Fact]
    public void A_website_order_paid_but_not_yet_packed_counts_on_no_day()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var o = Add(t, u, OrderChannel.Online, paid: true,
            created: Tue.AddHours(9), paidAt: Tue.AddHours(9));

        Assert.DoesNotContain(o.OrderNumber, OnTuesday(t));
        Assert.DoesNotContain(o.OrderNumber,
            t.Db.Orders.Where(EodDayRule.InWindow(Mon, Wed)).Select(x => x.OrderNumber).ToList());
    }

    [Fact]
    public void A_pickup_order_marked_ready_counts_like_a_packed_one()
    {
        // The EOD already treats "ready for pickup" as packed, so the day rule has to agree with it -
        // otherwise an order shows as Packed on the report but is missing from its totals.
        using var t = new TestDb();
        var u = t.SeedUser();
        var o = Add(t, u, OrderChannel.Online, paid: true,
            created: Mon, paidAt: Mon, pickupReadyAt: Tue.AddHours(14));

        Assert.Contains(o.OrderNumber, OnTuesday(t));
    }

    [Fact]
    public void A_counter_sale_still_counts_on_the_day_it_was_paid()
    {
        // POS sales never get packed; dating them by PackedAt would drop every counter sale from the
        // report and break the cash reconciliation.
        using var t = new TestDb();
        var u = t.SeedUser();
        var o = Add(t, u, OrderChannel.Pos, paid: true, created: Tue.AddHours(12), paidAt: Tue.AddHours(12));

        Assert.Contains(o.OrderNumber, OnTuesday(t));
    }

    [Fact]
    public void A_counter_sale_from_before_PaidAt_existed_falls_back_to_its_created_date()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var o = Add(t, u, OrderChannel.Pos, paid: true, created: Tue.AddHours(8), paidAt: null);

        Assert.Contains(o.OrderNumber, OnTuesday(t));
    }

    [Fact]
    public void Unpaid_orders_never_count_however_they_are_dated()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var pos = Add(t, u, OrderChannel.Pos, paid: false, created: Tue, paidAt: Tue);
        var web = Add(t, u, OrderChannel.Online, paid: false, created: Mon, packedAt: Tue);

        var tue = OnTuesday(t);
        Assert.DoesNotContain(pos.OrderNumber, tue);
        Assert.DoesNotContain(web.OrderNumber, tue);
    }

    [Fact]
    public void The_window_is_half_open_so_a_day_boundary_lands_on_exactly_one_day()
    {
        using var t = new TestDb();
        var u = t.SeedUser();
        var midnightTue = Add(t, u, OrderChannel.Online, paid: true, created: Mon, packedAt: Tue);
        var midnightWed = Add(t, u, OrderChannel.Online, paid: true, created: Mon, packedAt: Wed);

        var tue = OnTuesday(t);
        Assert.Contains(midnightTue.OrderNumber, tue);     // start of the window is included
        Assert.DoesNotContain(midnightWed.OrderNumber, tue); // the end is not
    }
}
