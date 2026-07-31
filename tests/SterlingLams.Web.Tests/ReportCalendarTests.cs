using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// Money reports used UTC days and dated revenue by when an order was CREATED. Both were wrong for
/// this business: the shop trades in Lagos (UTC+1), and an order placed one day but paid another —
/// a bank transfer confirmed by staff later — belongs to the day the money arrived.
/// </summary>
public class ReportCalendarTests
{
    [Fact]
    public void A_sale_just_after_midnight_in_lagos_belongs_to_that_day_not_the_previous_one()
    {
        // 00:30 on 15 July in Lagos is 23:30 on 14 July UTC. Bucketing on the UTC date put this
        // sale in the 14th's takings.
        var utc = new DateTime(2026, 7, 14, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 7, 15), ReportCalendar.LocalDay(utc));
    }

    [Fact]
    public void A_sale_just_before_midnight_in_lagos_stays_on_that_day()
    {
        // 23:30 on 15 July in Lagos = 22:30 UTC the same day.
        var utc = new DateTime(2026, 7, 15, 22, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 7, 15), ReportCalendar.LocalDay(utc));
    }

    [Fact]
    public void Range_covers_the_lagos_day_which_starts_an_hour_before_utc_midnight()
    {
        var (fromUtc, toUtc, fromLocal, toLocal) = ReportCalendar.Range("2026-07-15", "2026-07-15");

        Assert.Equal(new DateTime(2026, 7, 14, 23, 0, 0, DateTimeKind.Utc), fromUtc);
        Assert.Equal(new DateTime(2026, 7, 15, 23, 0, 0, DateTimeKind.Utc), toUtc);
        // The local dates are what the screen shows — inclusive, and unshifted.
        Assert.Equal(new DateTime(2026, 7, 15), fromLocal);
        Assert.Equal(new DateTime(2026, 7, 15), toLocal);
    }

    [Fact]
    public void Range_defaults_to_the_last_thirty_lagos_days_and_never_ends_before_it_starts()
    {
        var (_, _, fromLocal, toLocal) = ReportCalendar.Range(null, null);
        Assert.Equal(ReportCalendar.Today, toLocal);
        Assert.Equal(ReportCalendar.Today.AddDays(-29), fromLocal);

        // A backwards range collapses to a single day rather than returning nothing.
        var (_, _, f2, t2) = ReportCalendar.Range("2026-07-20", "2026-07-01");
        Assert.Equal(f2, t2);
    }

    // ── Revenue is dated by payment ────────────────────────────────────────────

    private static Order Sale(string userId, DateTime createdUtc, DateTime? paidUtc, decimal total) => new()
    {
        OrderNumber = "T-" + Guid.NewGuid().ToString("N")[..10],
        UserId = userId,
        Channel = OrderChannel.Online,
        FulfillmentType = FulfillmentType.Delivery,
        Status = OrderStatus.Confirmed,
        Currency = "NGN",
        Subtotal = total,
        Total = total,
        CreatedAt = createdUtc,
        IsPaid = paidUtc != null,
        PaidAt = paidUtc,
    };

    [Fact]
    public async Task Revenue_counts_in_the_period_the_money_arrived_not_when_the_order_was_placed()
    {
        using var t = new TestDb();
        var user = t.SeedUser();

        var placed = new DateTime(2026, 6, 29, 10, 0, 0, DateTimeKind.Utc);   // June
        var paid = new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc);      // paid in July

        t.Db.Orders.Add(Sale(user.Id, placed, paid, 40_000m));
        await t.Db.SaveChangesAsync();

        var (julyFrom, julyTo, _, _) = ReportCalendar.Range("2026-07-01", "2026-07-31");
        var (juneFrom, juneTo, _, _) = ReportCalendar.Range("2026-06-01", "2026-06-30");

        // The same predicate the reports use.
        var july = await t.Db.Orders
            .Where(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= julyFrom && (o.PaidAt ?? o.CreatedAt) < julyTo)
            .SumAsync(o => (decimal?)o.Total) ?? 0m;
        var june = await t.Db.Orders
            .Where(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= juneFrom && (o.PaidAt ?? o.CreatedAt) < juneTo)
            .SumAsync(o => (decimal?)o.Total) ?? 0m;

        Assert.Equal(40_000m, july);
        Assert.Equal(0m, june);   // it used to land here, before the money existed
    }

    [Fact]
    public async Task Older_paid_rows_with_no_PaidAt_still_count_on_their_created_date()
    {
        using var t = new TestDb();
        var user = t.SeedUser();

        // Rows written before PaidAt existed: paid, but with no payment timestamp. Filtering on
        // PaidAt alone would have made this revenue disappear from every report.
        var when = new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc);
        var legacy = Sale(user.Id, when, null, 25_000m);
        legacy.IsPaid = true;
        t.Db.Orders.Add(legacy);
        await t.Db.SaveChangesAsync();

        var (from, to, _, _) = ReportCalendar.Range("2026-07-01", "2026-07-31");
        var total = await t.Db.Orders
            .Where(o => o.IsPaid && (o.PaidAt ?? o.CreatedAt) >= from && (o.PaidAt ?? o.CreatedAt) < to)
            .SumAsync(o => (decimal?)o.Total) ?? 0m;

        Assert.Equal(25_000m, total);
    }
}
