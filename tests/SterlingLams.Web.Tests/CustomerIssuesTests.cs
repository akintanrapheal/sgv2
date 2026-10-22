using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Areas.Admin.Controllers;
using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Admin → Customer Issues monitor: the dashboard counts issues by type/time, lists them
/// (unresolved first), filters, and summarises abandoned payments.</summary>
public class CustomerIssuesTests
{
    private static void Add(TestDb t, StorefrontIssueType type, DateTime when, bool resolved = false, string reason = "x")
        => t.Db.StorefrontIssues.Add(new StorefrontIssue { Type = type, Reason = reason, CreatedAt = when, Resolved = resolved });

    private static async Task<CustomerIssuesController.CustomerIssuesVm> LoadAsync(TestDb t,
        string type = "", string resolved = "")
    {
        var ctrl = new CustomerIssuesController(t.Db);
        var result = await ctrl.Index(type: type, resolved: resolved);
        return (CustomerIssuesController.CustomerIssuesVm)((ViewResult)result).ViewData.Model!;
    }

    [Fact]
    public async Task Counts_today_week_and_unresolved_by_type()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        Add(t, StorefrontIssueType.AddToCart, now);                       // today
        Add(t, StorefrontIssueType.AddToCart, now.AddMinutes(-5));        // today
        Add(t, StorefrontIssueType.Payment, now, resolved: true);        // today, resolved
        Add(t, StorefrontIssueType.Payment, now.AddDays(-3));            // this week
        Add(t, StorefrontIssueType.ServerError, now.AddDays(-10));       // outside the 7-day window
        await t.Db.SaveChangesAsync();

        var vm = await LoadAsync(t);

        Assert.Equal(3, vm.TodayTotal);                                  // 2 cart + 1 payment today
        Assert.Equal(4, vm.WeekTotal);                                   // excludes the 10-day-old one
        Assert.Equal(2, vm.TodayByType[StorefrontIssueType.AddToCart]);
        Assert.Equal(4, vm.Unresolved);                                  // all but the resolved payment
        Assert.Equal(14, vm.Trend.Count);                               // 14-day trend series
    }

    [Fact]
    public async Task Filters_by_type_and_resolved_state()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        Add(t, StorefrontIssueType.AddToCart, now);
        Add(t, StorefrontIssueType.Payment, now);
        Add(t, StorefrontIssueType.Payment, now.AddMinutes(-1), resolved: true);
        await t.Db.SaveChangesAsync();

        Assert.Equal(2, (await LoadAsync(t, type: "Payment")).Rows.Count);
        Assert.Single((await LoadAsync(t, type: "AddToCart")).Rows);
        Assert.Equal(2, (await LoadAsync(t, resolved: "open")).Rows.Count);
        Assert.Single((await LoadAsync(t, resolved: "done")).Rows);
    }

    [Fact]
    public async Task Unresolved_issues_are_listed_first()
    {
        using var t = new TestDb();
        var now = DateTime.UtcNow;
        Add(t, StorefrontIssueType.AddToCart, now, resolved: true, reason: "old_resolved");
        Add(t, StorefrontIssueType.AddToCart, now.AddMinutes(-30), resolved: false, reason: "open_one");
        await t.Db.SaveChangesAsync();

        var vm = await LoadAsync(t);
        Assert.False(vm.Rows.First().Resolved);   // the unresolved one sorts first even though it's older
    }

    [Fact]
    public async Task Summarises_abandoned_payments_from_orders()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var u = t.SeedUser();

        // Unpaid, auto-cancelled online order within the week = an abandoned payment.
        t.Db.Orders.Add(new Order
        {
            OrderNumber = "W-AB", UserId = u.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = allen.Id,
            Status = OrderStatus.Cancelled, IsPaid = false, CreatedAt = DateTime.UtcNow.AddHours(-2),
            Currency = "NGN", Subtotal = 15000m, Total = 15000m,
        });
        await t.Db.SaveChangesAsync();

        var vm = await LoadAsync(t);
        Assert.Equal(1, vm.AbandonedCount);
        Assert.Equal(15000m, vm.AbandonedValue);
    }
}
