using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>
/// The end-of-day cash-up splits an online order's ITEM revenue across the branches that supplied the
/// goods: the fulfilling branch earns its own-stock items, and a branch that packs & sends items to be
/// merged earns those item prices. Delivery is never counted (Logistics keeps it).
/// </summary>
public class CashUpSplitTests
{
    private static TillSession OpenSession(TestDb t, Store store, DateTime openedAt)
    {
        // These tests assert each branch's share of WEBSITE revenue, which now lands on the branch's
        // designated online till — so the session's register is that online till.
        var reg = new Register { Name = store.Name + " Pos 1", StoreId = store.Id, IsActive = true, HandlesOnlineOrders = true };
        t.Db.Add(reg);
        t.Db.SaveChanges();
        var session = new TillSession { RegisterId = reg.Id, OpenedByUserId = "sys", OpenedAt = openedAt };
        t.Db.Add(session);
        t.Db.SaveChanges();
        session.Register = reg; // BuildAsync reads session.Register.StoreId
        return session;
    }

    [Fact]
    public async Task Online_order_item_revenue_splits_across_supplying_branches_delivery_excluded()
    {
        using var t = new TestDb();
        var (_, allen, ikota) = t.SeedBranches();
        var buyer = t.SeedUser();
        var pA = t.SeedProduct(1000m);   // 3 from Allen's own stock
        var pB = t.SeedProduct(2000m);   // 2 sent by Ikota to merge

        var now = DateTime.UtcNow;
        var allenSession = OpenSession(t, allen, now.AddHours(-2));
        var ikotaSession = OpenSession(t, ikota, now.AddHours(-2));

        // Paid online delivery order, fulfilled by Allen, with a ₦500 delivery fee that must NOT be counted.
        var order = new Order
        {
            OrderNumber = "T-SPLIT-1",
            UserId = buyer.Id,
            Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery,
            FulfillingStoreId = allen.Id,
            Status = OrderStatus.Processing,
            IsPaid = true,
            PaidAt = now.AddMinutes(-60),
            Currency = "NGN",
            Subtotal = 3 * 1000m + 2 * 2000m,
            DeliveryFee = 500m,
            Total = 3 * 1000m + 2 * 2000m + 500m,
            Items = new List<OrderItem>
            {
                new() { ProductId = pA.Id, ProductName = pA.Name, Quantity = 3, UnitPrice = 1000m },
                new() { ProductId = pB.Id, ProductName = pB.Name, Quantity = 2, UnitPrice = 2000m },
            }
        };
        t.Db.Add(order);
        t.Db.SaveChanges();

        // Ikota packed its 2× pB and sent them to Allen to merge (dispatched inside Ikota's session).
        var transfer = new StockTransfer
        {
            TransferNumber = "TRF-1",
            FromStoreId = ikota.Id,
            ToStoreId = allen.Id,
            OrderId = order.Id,
            Status = TransferStatus.InTransit,
            DispatchedAt = now.AddMinutes(-30),
            Items = new List<StockTransferItem>
            {
                new() { ProductId = pB.Id, ProductName = pB.Name, RequestedQty = 2, ApprovedQty = 2, DispatchedQty = 2 },
            }
        };
        t.Db.Add(transfer);
        t.Db.SaveChanges();

        var cash = new CashUpService(t.Db);

        var allenVm = await cash.BuildAsync(allenSession, interim: true, currentUserId: "");
        var ikotaVm = await cash.BuildAsync(ikotaSession, interim: true, currentUserId: "");

        // This order wasn't a manual bank-transfer, so its revenue lands in the "paid online" bucket.
        decimal Website(CloseSummaryVm vm) => vm.Tenders.First(x => x.Key == "WebsiteOnline").Expected;

        // Allen: only its own 3× pB... its own 3× pA = ₦3,000 (the 2× pB came from Ikota, so excluded).
        Assert.Equal(3000m, Website(allenVm));
        // Ikota: the 2× pB it sent to merge = ₦4,000.
        Assert.Equal(4000m, Website(ikotaVm));
        // Together they equal the order's ITEM total — delivery (₦500) counted by neither.
        Assert.Equal(order.Subtotal, Website(allenVm) + Website(ikotaVm));
        // Nothing in the bank-transfer bucket for this gateway order.
        Assert.Equal(0m, allenVm.Tenders.First(x => x.Key == "WebsiteTransfer").Expected);

        // Only cash is counted at close; every other channel is auto-recorded (not countable).
        Assert.True(allenVm.Tenders.Single(x => x.Key == "Cash").Countable);
        Assert.All(allenVm.Tenders.Where(x => x.Key != "Cash"), t => Assert.False(t.Countable));

        // Each branch's EOD records it as a transaction and shows the right items.
        Assert.Equal(1, allenVm.Transactions);
        Assert.Equal(1, ikotaVm.Transactions);
        Assert.Contains(allenVm.ByProduct, r => r.Name == pA.Name && r.Qty == 3);
        Assert.DoesNotContain(allenVm.ByProduct, r => r.Name == pB.Name);
        Assert.Contains(ikotaVm.ByProduct, r => r.Name == pB.Name && r.Qty == 2);
    }

    [Fact]
    public async Task Customer_care_bank_transfer_website_order_lands_in_the_transfer_bucket()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var buyer = t.SeedUser();
        var p = t.SeedProduct(2500m);
        var now = DateTime.UtcNow;
        var session = OpenSession(t, allen, now.AddHours(-2));

        // A website order customer care confirmed manually as a bank transfer (PaymentProvider = "Transfer"),
        // fulfilled by Allen from its own stock.
        var order = new Order
        {
            OrderNumber = "T-WT-1", UserId = buyer.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = allen.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = now.AddMinutes(-30),
            PaymentProvider = "Transfer", Currency = "NGN",
            Subtotal = 2 * 2500m, DeliveryFee = 500m, Total = 2 * 2500m + 500m,
            Items = new List<OrderItem> { new() { ProductId = p.Id, ProductName = p.Name, Quantity = 2, UnitPrice = 2500m } },
        };
        t.Db.Add(order);
        t.Db.SaveChanges();

        var vm = await new CashUpService(t.Db).BuildAsync(session, interim: true, currentUserId: "");

        // ₦5,000 of items lands in "Website – bank transfer", nothing in "paid online"; delivery excluded.
        Assert.Equal(5000m, vm.Tenders.First(x => x.Key == "WebsiteTransfer").Expected);
        Assert.Equal(0m, vm.Tenders.First(x => x.Key == "WebsiteOnline").Expected);
    }

    [Fact]
    public async Task Manually_confirmed_transfer_website_order_also_lands_in_the_transfer_bucket()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var buyer = t.SeedUser();
        var p = t.SeedProduct(2500m);
        var now = DateTime.UtcNow;
        var session = OpenSession(t, allen, now.AddHours(-2));

        // A staff-confirmed bank-transfer website order carries PaymentProvider "Manual (Transfer)" — it must
        // still count as "Website – bank transfer", not "paid online".
        var order = new Order
        {
            OrderNumber = "T-WT-MAN", UserId = buyer.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = allen.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = now.AddMinutes(-30),
            PaymentProvider = "Manual (Transfer)", Currency = "NGN",
            Subtotal = 2 * 2500m, DeliveryFee = 500m, Total = 2 * 2500m + 500m,
            Items = new List<OrderItem> { new() { ProductId = p.Id, ProductName = p.Name, Quantity = 2, UnitPrice = 2500m } },
        };
        t.Db.Add(order);
        t.Db.SaveChanges();

        var vm = await new CashUpService(t.Db).BuildAsync(session, interim: true, currentUserId: "");

        Assert.Equal(5000m, vm.Tenders.First(x => x.Key == "WebsiteTransfer").Expected);
        Assert.Equal(0m, vm.Tenders.First(x => x.Key == "WebsiteOnline").Expected);
    }

    [Fact]
    public async Task Refunds_net_against_the_channel_they_were_paid_back_through()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var buyer = t.SeedUser();
        var p = t.SeedProduct(1000m);
        var now = DateTime.UtcNow;
        var session = OpenSession(t, allen, now.AddHours(-2));

        // Two in-store sales on this till: one paid by card, one by cash (no OrderPayments rows → the
        // PaymentProvider fallback drives SumOf).
        Order PosSale(string num, string method, decimal total) => new()
        {
            OrderNumber = num, UserId = buyer.Id, Channel = OrderChannel.Pos, TillSessionId = session.Id,
            FulfillingStoreId = allen.Id, Status = OrderStatus.Delivered, IsPaid = true, PaidAt = now.AddMinutes(-40),
            PaymentProvider = method, Currency = "NGN", Subtotal = total, Total = total,
            Items = new List<OrderItem> { new() { ProductId = p.Id, ProductName = p.Name, Quantity = (int)(total / 1000m), UnitPrice = 1000m } },
        };
        var cardSale = PosSale("T-CARD", "Card", 5000m);
        var cashSale = PosSale("T-CASH", "Cash", 3000m);
        t.Db.AddRange(cardSale, cashSale);
        t.Db.SaveChanges();

        // Approved refunds: ₦2,000 back on the card, ₦1,000 back in cash.
        t.Db.AddRange(
            new Refund { RefundNumber = "R-CARD", OriginalOrderId = cardSale.Id, TillSessionId = session.Id,
                RefundMethod = "Card", Amount = 2000m, Status = RefundStatus.Approved, CreatedAt = now.AddMinutes(-10) },
            new Refund { RefundNumber = "R-CASH", OriginalOrderId = cashSale.Id, TillSessionId = session.Id,
                RefundMethod = "Cash", Amount = 1000m, Status = RefundStatus.Approved, CreatedAt = now.AddMinutes(-5) });
        t.Db.SaveChanges();

        var vm = await new CashUpService(t.Db).BuildAsync(session, interim: true, currentUserId: "");

        // Card: 5,000 − 2,000 = 3,000. Cash: 3,000 − 1,000 = 2,000. Transfer untouched.
        Assert.Equal(3000m, vm.Tenders.First(x => x.Key == "Card").Expected);
        Assert.Equal(2000m, vm.Tenders.First(x => x.Key == "Cash").Expected);
        Assert.Equal(0m, vm.Tenders.First(x => x.Key == "Transfer").Expected);

        // Both POS refunds show on the Refunds tab.
        Assert.Contains(vm.Refunds, r => r.Number == "R-CARD" && r.Amount == 2000m);
        Assert.Contains(vm.Refunds, r => r.Number == "R-CASH" && r.Amount == 1000m);
    }

    [Fact]
    public async Task Website_refund_reduces_the_branch_figure_and_shows_on_the_refunds_tab()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var buyer = t.SeedUser();
        var p = t.SeedProduct(1000m);
        var now = DateTime.UtcNow;
        var session = OpenSession(t, allen, now.AddHours(-2));

        // Paid online order fulfilled by Allen: 2× p = ₦2,000 to Allen's website figure.
        var order = new Order
        {
            OrderNumber = "T-WEB-R", UserId = buyer.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = allen.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = now.AddMinutes(-60),
            Currency = "NGN", Subtotal = 2000m, Total = 2000m,
            Items = new List<OrderItem> { new() { ProductId = p.Id, ProductName = p.Name, Quantity = 2, UnitPrice = 1000m } },
        };
        t.Db.Add(order);
        t.Db.SaveChanges();

        // Approved WEBSITE refund (no till session) for 1 unit, approved inside Allen's window.
        var refund = new Refund
        {
            RefundNumber = "R-WEB", OriginalOrderId = order.Id, TillSessionId = null,
            RefundMethod = "Paystack", Amount = 1000m, Status = RefundStatus.Approved,
            CreatedAt = now.AddMinutes(-20), DecisionAt = now.AddMinutes(-15),
            Items = new List<RefundItem> { new() { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 1000m } },
        };
        t.Db.Add(refund);
        t.Db.SaveChanges();

        var vm = await new CashUpService(t.Db).BuildAsync(session, interim: true, currentUserId: "");

        // 2,000 earned − 1,000 refunded = 1,000 left in the website figure.
        Assert.Equal(1000m, vm.Tenders.First(x => x.Key == "WebsiteOnline").Expected);
        // And the website refund now appears on the Refunds tab (was previously only netted, not listed).
        Assert.Contains(vm.Refunds, r => r.Number == "R-WEB" && r.Amount == 1000m && r.Method.StartsWith("Website"));
    }

    [Fact]
    public async Task Refund_exceeding_a_channels_sales_floors_it_to_zero_and_shows_a_refunds_line()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var buyer = t.SeedUser();
        var p = t.SeedProduct(5000m);
        var now = DateTime.UtcNow;
        var session = OpenSession(t, allen, now.AddHours(-2));

        // In-store card sale this session, so there are positive takings to net against.
        var pos = new Order
        {
            OrderNumber = "T-CARD", UserId = buyer.Id, Channel = OrderChannel.Pos, TillSessionId = session.Id,
            FulfillingStoreId = allen.Id, Status = OrderStatus.Delivered, IsPaid = true, PaidAt = now.AddMinutes(-30),
            PaymentProvider = "Card", Currency = "NGN", Subtotal = 10000m, Total = 10000m,
            Items = new List<OrderItem> { new() { ProductId = p.Id, ProductName = p.Name, Quantity = 2, UnitPrice = 5000m } },
        };
        // Online order Allen fulfilled on an EARLIER day (paid before this session → not website sales this session)...
        var web = new Order
        {
            OrderNumber = "T-WEBOLD", UserId = buyer.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = allen.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = now.AddHours(-26),
            PaymentProvider = "Paystack", Currency = "NGN", Subtotal = 5000m, Total = 5000m,
            Items = new List<OrderItem> { new() { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 5000m } },
        };
        t.Db.AddRange(pos, web);
        t.Db.SaveChanges();

        // ...but REFUNDED during this session → the refund is attributed to Allen with no matching sale this session.
        var refund = new Refund
        {
            RefundNumber = "R-OLD", OriginalOrderId = web.Id, TillSessionId = null,
            RefundMethod = "Paystack", Amount = 5000m, Status = RefundStatus.Approved,
            CreatedAt = now.AddMinutes(-20), DecisionAt = now.AddMinutes(-15),
            Items = new List<RefundItem> { new() { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 5000m } },
        };
        t.Db.Add(refund);
        t.Db.SaveChanges();

        var vm = await new CashUpService(t.Db).BuildAsync(session, interim: true, currentUserId: "");

        // The website channel is floored at 0 (not -5,000); the excess shows as an explicit refunds line.
        Assert.Equal(0m, vm.Tenders.First(x => x.Key == "WebsiteOnline").Expected);
        Assert.Contains(vm.Tenders, x => x.Key == "RefundsBeyondSales" && x.Expected == -5000m);
        // No SALES channel reads negative.
        Assert.DoesNotContain(vm.Tenders.Where(x => x.Key != "RefundsBeyondSales"), x => x.Expected < 0);
        // Takings unchanged by the cosmetic split: card 10,000 − 5,000 refund = 5,000.
        Assert.Equal(5000m, vm.Takings);
    }
}
