using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Website (online-paid) revenue must land on a store's designated ONLINE TILL only — not on
/// every till open at the branch (which double-counted it and broke the Z-report balance).</summary>
public class CashUpServiceOnlineTillTests
{
    private async Task<TillSession> OpenSessionAsync(TestDb t, int registerId, DateTime openedAt)
    {
        var s = new TillSession { RegisterId = registerId, OpenedByUserId = "staff", OpenedAt = openedAt, OpeningFloat = 0 };
        t.Db.TillSessions.Add(s);
        await t.Db.SaveChangesAsync();
        return await t.Db.TillSessions.Include(x => x.Register).FirstAsync(x => x.Id == s.Id);
    }

    [Fact]
    public async Task Website_revenue_shows_on_the_online_till_only()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (_, allen, _) = t.SeedBranches();
        var p = t.SeedProduct(price: 5000m);

        // Two tills at the same branch: Till 1 is the online till, Till 2 is in-store only.
        var onlineReg = new Register { Name = "Till 1", StoreId = allen.Id, IsActive = true, HandlesOnlineOrders = true };
        var inStoreReg = new Register { Name = "Till 2", StoreId = allen.Id, IsActive = true, HandlesOnlineOrders = false };
        t.Db.Registers.AddRange(onlineReg, inStoreReg);
        await t.Db.SaveChangesAsync();

        var openedAt = DateTime.UtcNow.AddHours(-2);
        var onlineSession = await OpenSessionAsync(t, onlineReg.Id, openedAt);
        var inStoreSession = await OpenSessionAsync(t, inStoreReg.Id, openedAt);

        // A paid website order fulfilled by this branch, within both open sessions' windows.
        t.Db.Orders.Add(new Order
        {
            OrderNumber = "W-" + Guid.NewGuid().ToString("N")[..8],
            UserId = user.Id,
            Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery,
            Status = OrderStatus.Confirmed,
            IsPaid = true,
            PaidAt = DateTime.UtcNow.AddHours(-1),
            PackedAt = DateTime.UtcNow.AddMinutes(-30),   // packed within the open session's window
            PaymentProvider = "Paystack",
            Currency = "NGN",
            Subtotal = 5000m,
            Total = 5000m,
            FulfillingStoreId = allen.Id,
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 5000m } }
        });
        await t.Db.SaveChangesAsync();

        var svc = new CashUpService(t.Db);
        var onlineVm = await svc.BuildAsync(onlineSession, interim: true, currentUserId: "");
        var inStoreVm = await svc.BuildAsync(inStoreSession, interim: true, currentUserId: "");

        decimal Web(CloseSummaryVm vm) => vm.Tenders.First(x => x.Key == "WebsiteOnline").Expected;

        // Online till: counts the website sale once. In-store till: nothing.
        Assert.Equal(5000m, Web(onlineVm));
        Assert.Equal(1, onlineVm.Transactions);
        Assert.Equal(0m, Web(inStoreVm));
        Assert.Equal(0, inStoreVm.Transactions);
    }

    [Fact]
    public async Task A_voided_website_order_is_excluded_from_the_cash_up()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (_, allen, _) = t.SeedBranches();
        var p = t.SeedProduct(price: 5000m);

        var onlineReg = new Register { Name = "Till 1", StoreId = allen.Id, IsActive = true, HandlesOnlineOrders = true };
        t.Db.Registers.Add(onlineReg);
        await t.Db.SaveChangesAsync();
        var session = await OpenSessionAsync(t, onlineReg.Id, DateTime.UtcNow.AddHours(-2));

        // A paid website order that was later VOIDED (stock returned, moved to Cancelled, no refund).
        t.Db.Orders.Add(new Order
        {
            OrderNumber = "W-VOID", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, Status = OrderStatus.Cancelled,
            IsPaid = true, PaidAt = DateTime.UtcNow.AddHours(-1), PackedAt = DateTime.UtcNow.AddMinutes(-40),
            PaymentProvider = "Paystack",
            Currency = "NGN", Subtotal = 5000m, Total = 5000m, FulfillingStoreId = allen.Id,
            VoidedAt = DateTime.UtcNow.AddMinutes(-30), VoidedByName = "Manager", VoidReason = "Test order",
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 5000m } }
        });
        await t.Db.SaveChangesAsync();

        var vm = await new CashUpService(t.Db).BuildAsync(session, interim: true, currentUserId: "");

        // The voided order contributes nothing to the website takings or the transaction count.
        Assert.Equal(0m, vm.Tenders.First(x => x.Key == "WebsiteOnline").Expected);
        Assert.Equal(0, vm.Transactions);
    }

    [Fact]
    public async Task Website_order_packed_before_the_till_opened_still_counts()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, _, _) = t.SeedBranches();
        var p = t.SeedProduct(24450m);
        var reg = new Register { Name = "Abuja Pos 1", StoreId = abuja.Id, IsActive = true, HandlesOnlineOrders = true };
        t.Db.Registers.Add(reg);
        await t.Db.SaveChangesAsync();

        // A concrete PAST Lagos day (yesterday) so the whole window sits before "now" and the
        // result is deterministic regardless of the wall-clock time the test runs at.
        var day = SterlingLams.Web.Services.ReportCalendar.Today.AddDays(-1);
        var dayStart = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(day);
        var opened = dayStart.AddHours(9).AddMinutes(24);     // till opened 09:24
        var closed = dayStart.AddHours(19);                   // closed 19:00
        var packedEarly = dayStart.AddHours(7).AddMinutes(44);// order packed 07:44 — BEFORE the till opened

        var session = new TillSession { RegisterId = reg.Id, OpenedByUserId = "dorathy", OpenedAt = opened, ClosedAt = closed, OpeningFloat = 0 };
        t.Db.TillSessions.Add(session);
        t.Db.Orders.Add(new Order
        {
            OrderNumber = "W-EARLY", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = abuja.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = dayStart.AddHours(6), PackedAt = packedEarly,
            PaymentProvider = "Paystack", Currency = "NGN", Subtotal = 24450m, DeliveryFee = 3500m, Total = 27950m,
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 24450m } }
        });
        await t.Db.SaveChangesAsync();

        var tracked = await t.Db.TillSessions.Include(x => x.Register).FirstAsync(x => x.Id == session.Id);
        var vm = await new CashUpService(t.Db).BuildAsync(tracked, interim: false, currentUserId: "");

        // Counted despite being packed before the till opened; merchandise only (delivery excluded).
        Assert.Equal(24450m, vm.Tenders.First(x => x.Key == "WebsiteOnline").Expected);
        Assert.Equal(1, vm.Transactions);
    }

    [Fact]
    public async Task Website_order_paid_one_day_but_packed_the_next_counts_on_the_packed_day()
    {
        // The exact reported case: an order came in (paid) one day and the branch packed it the next morning.
        // It must land on the EPOS cash-up for the day it was PACKED (matching Finance's EOD), not the paid day.
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, _, _) = t.SeedBranches();
        var p = t.SeedProduct(10000m);
        var reg = new Register { Name = "Abuja Pos 1", StoreId = abuja.Id, IsActive = true, HandlesOnlineOrders = true };
        t.Db.Registers.Add(reg);
        await t.Db.SaveChangesAsync();

        // Two fully-past Lagos days so both windows sit before "now" (deterministic whatever time this runs).
        var paidDay = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(SterlingLams.Web.Services.ReportCalendar.Today.AddDays(-2));
        var packedDay = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(SterlingLams.Web.Services.ReportCalendar.Today.AddDays(-1));

        var paidDaySession   = new TillSession { RegisterId = reg.Id, OpenedByUserId = "a", OpenedAt = paidDay.AddHours(9),   ClosedAt = paidDay.AddHours(19) };
        var packedDaySession = new TillSession { RegisterId = reg.Id, OpenedByUserId = "a", OpenedAt = packedDay.AddHours(9), ClosedAt = packedDay.AddHours(19) };
        t.Db.TillSessions.AddRange(paidDaySession, packedDaySession);
        t.Db.Orders.Add(new Order
        {
            OrderNumber = "SGW-XDAY", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = abuja.Id,
            Status = OrderStatus.Processing, IsPaid = true,
            PaidAt = paidDay.AddHours(15), PackedAt = packedDay.AddHours(11),
            PaymentProvider = "Paystack", Currency = "NGN", Subtotal = 10000m, DeliveryFee = 2000m, Total = 12000m,
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 10000m } }
        });
        await t.Db.SaveChangesAsync();

        var svc = new CashUpService(t.Db);
        var paidVm = await svc.BuildAsync(await t.Db.TillSessions.Include(x => x.Register).FirstAsync(x => x.Id == paidDaySession.Id), interim: false, currentUserId: "");
        var packedVm = await svc.BuildAsync(await t.Db.TillSessions.Include(x => x.Register).FirstAsync(x => x.Id == packedDaySession.Id), interim: false, currentUserId: "");

        decimal Web(CloseSummaryVm vm) => vm.Tenders.First(x => x.Key == "WebsiteOnline").Expected;
        Assert.Equal(0m, Web(paidVm));          // NOT on the day it was paid
        Assert.Equal(10000m, Web(packedVm));    // ON the day it was packed (merchandise only, delivery excluded)
        Assert.Equal(1, packedVm.Transactions);
    }

    [Fact]
    public async Task Website_order_packed_after_the_till_closed_still_counts()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, _, _) = t.SeedBranches();
        var p = t.SeedProduct(16950m);
        var reg = new Register { Name = "Allen Pos 1", StoreId = abuja.Id, IsActive = true, HandlesOnlineOrders = true };
        t.Db.Registers.Add(reg);
        await t.Db.SaveChangesAsync();

        // Anchor to a past Lagos day (yesterday) so the "after close" timestamp still sits before
        // "now" — otherwise this fails whenever the test runs earlier in the day than 13:12.
        var day = SterlingLams.Web.Services.ReportCalendar.Today.AddDays(-1);
        var dayStart = SterlingLams.Web.Services.ReportCalendar.StartOfDayUtc(day);
        var opened = dayStart.AddHours(9);                   // till opened 09:00
        var closed = dayStart.AddHours(13);                  // closed early at 13:00
        var packedLate = dayStart.AddHours(13).AddMinutes(12);// packed 13:12 — AFTER the early close

        var session = new TillSession { RegisterId = reg.Id, OpenedByUserId = "lucy", OpenedAt = opened, ClosedAt = closed, OpeningFloat = 0 };
        t.Db.TillSessions.Add(session);
        t.Db.Orders.Add(new Order
        {
            OrderNumber = "SGW-LATE", UserId = user.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = abuja.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = dayStart.AddHours(10), PackedAt = packedLate,
            PaymentProvider = "Manual (Transfer)", Currency = "NGN", Subtotal = 16950m, DeliveryFee = 7500m, Total = 24450m,
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 16950m } }
        });
        await t.Db.SaveChangesAsync();

        var tracked = await t.Db.TillSessions.Include(x => x.Register).FirstAsync(x => x.Id == session.Id);
        var vm = await new CashUpService(t.Db).BuildAsync(tracked, interim: false, currentUserId: "");

        // A website order packed after the till closed still lands in the day's cash-up.
        Assert.Equal(16950m, vm.Tenders.First(x => x.Key == "WebsiteTransfer").Expected);
        Assert.Equal(1, vm.Transactions);
    }
}
