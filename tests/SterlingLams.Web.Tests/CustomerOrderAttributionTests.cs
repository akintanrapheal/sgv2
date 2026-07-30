using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// A customer's order count and spend must include BOTH channels: an online order links the buyer
/// through Order.UserId, while a POS sale puts the CASHIER there and the buyer in Order.CustomerUserId.
/// The admin Customers list/detail/export used to count only UserId, so in-store regulars showed zero
/// orders and never reached the VIP segment. These tests pin the attribution and, just as importantly,
/// prove the correlated sub-queries the list projection uses actually translate to SQL.
/// </summary>
public class CustomerOrderAttributionTests
{
    private static Order Sale(string userId, string? customerUserId, OrderChannel channel, decimal total, bool paid)
        => new()
        {
            OrderNumber = "T-" + Guid.NewGuid().ToString("N")[..10],
            UserId = userId,
            CustomerUserId = customerUserId,
            Channel = channel,
            FulfillmentType = FulfillmentType.Delivery,
            Status = OrderStatus.Confirmed,
            Currency = "NGN",
            Subtotal = total,
            Total = total,
            IsPaid = paid,
            PaidAt = paid ? DateTime.UtcNow : null,
        };

    [Fact]
    public async Task Customer_totals_count_online_and_in_store_purchases()
    {
        using var t = new TestDb();
        var buyer = t.SeedUser();
        var cashier = t.SeedUser();

        t.Db.Orders.AddRange(
            Sale(buyer.Id, null, OrderChannel.Online, 10_000m, paid: true),        // bought online
            Sale(cashier.Id, buyer.Id, OrderChannel.Pos, 25_000m, paid: true),     // bought at the till
            Sale(cashier.Id, buyer.Id, OrderChannel.Pos, 5_000m, paid: false),     // counted, but not spend
            Sale(cashier.Id, null, OrderChannel.Pos, 99_000m, paid: true));        // a walk-in, not theirs
        await t.Db.SaveChangesAsync();

        // The same shape the admin Customers list projects — run it to prove EF can translate it.
        var all = t.Db.Orders.AsQueryable();
        var row = await t.Db.Users.Where(u => u.Id == buyer.Id)
            .Select(u => new
            {
                OrderCount = all.Count(o => (o.Channel == OrderChannel.Pos ? o.CustomerUserId : o.UserId) == u.Id),
                TotalSpend = all.Where(o => ((o.Channel == OrderChannel.Pos ? o.CustomerUserId : o.UserId) == u.Id) && o.IsPaid)
                                .Sum(o => (decimal?)o.Total) ?? 0m,
                LastOrderAt = all.Where(o => (o.Channel == OrderChannel.Pos ? o.CustomerUserId : o.UserId) == u.Id)
                                 .Max(o => (DateTime?)o.CreatedAt),
            })
            .SingleAsync();

        Assert.Equal(3, row.OrderCount);            // 1 online + 2 in-store; the walk-in is excluded
        Assert.Equal(35_000m, row.TotalSpend);      // paid only
        Assert.NotNull(row.LastOrderAt);
    }

    [Fact]
    public async Task Customer_with_no_orders_reports_zero_not_null()
    {
        using var t = new TestDb();
        var user = t.SeedUser();

        var all = t.Db.Orders.AsQueryable();
        var row = await t.Db.Users.Where(u => u.Id == user.Id)
            .Select(u => new
            {
                OrderCount = all.Count(o => (o.Channel == OrderChannel.Pos ? o.CustomerUserId : o.UserId) == u.Id),
                TotalSpend = all.Where(o => ((o.Channel == OrderChannel.Pos ? o.CustomerUserId : o.UserId) == u.Id) && o.IsPaid)
                                .Sum(o => (decimal?)o.Total) ?? 0m,
                LastOrderAt = all.Where(o => (o.Channel == OrderChannel.Pos ? o.CustomerUserId : o.UserId) == u.Id)
                                 .Max(o => (DateTime?)o.CreatedAt),
            })
            .SingleAsync();

        Assert.Equal(0, row.OrderCount);
        Assert.Equal(0m, row.TotalSpend);
        Assert.Null(row.LastOrderAt);
    }

    [Fact]
    public async Task Vip_and_repeat_segment_filters_see_in_store_spend()
    {
        using var t = new TestDb();
        var buyer = t.SeedUser();
        var cashier = t.SeedUser();

        // Everything bought at the till — nothing at all under the buyer's own UserId.
        t.Db.Orders.AddRange(
            Sale(cashier.Id, buyer.Id, OrderChannel.Pos, SterlingLams.Web.Areas.Admin.ViewModels.CustomerSegments.VipSpend, paid: true),
            Sale(cashier.Id, buyer.Id, OrderChannel.Pos, 1_000m, paid: true));
        await t.Db.SaveChangesAsync();

        var all = t.Db.Orders.AsQueryable();

        var vip = await t.Db.Users
            .Where(u => (all.Where(o => ((o.Channel == OrderChannel.Pos ? o.CustomerUserId : o.UserId) == u.Id) && o.IsPaid)
                            .Sum(o => (decimal?)o.Total) ?? 0m) >= SterlingLams.Web.Areas.Admin.ViewModels.CustomerSegments.VipSpend)
            .Select(u => u.Id).ToListAsync();

        var repeat = await t.Db.Users
            .Where(u => all.Count(o => (o.Channel == OrderChannel.Pos ? o.CustomerUserId : o.UserId) == u.Id) >= 2)
            .Select(u => u.Id).ToListAsync();

        Assert.Contains(buyer.Id, vip);
        Assert.Contains(buyer.Id, repeat);
        // The cashier rang these up; they didn't buy them. Matching Order.UserId on a POS sale would
        // hand every till sale to the cashier and turn staff accounts into VIP customers.
        Assert.DoesNotContain(cashier.Id, vip);
        Assert.DoesNotContain(cashier.Id, repeat);
    }
}
