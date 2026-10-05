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
        var reg = new Register { Name = store.Name + " Pos 1", StoreId = store.Id, IsActive = true };
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

        decimal Website(CloseSummaryVm vm) => vm.Tenders.First(x => x.Key == "Website").Expected;

        // Allen: only its own 3× pB... its own 3× pA = ₦3,000 (the 2× pB came from Ikota, so excluded).
        Assert.Equal(3000m, Website(allenVm));
        // Ikota: the 2× pB it sent to merge = ₦4,000.
        Assert.Equal(4000m, Website(ikotaVm));
        // Together they equal the order's ITEM total — delivery (₦500) counted by neither.
        Assert.Equal(order.Subtotal, Website(allenVm) + Website(ikotaVm));

        // Each branch's EOD records it as a transaction and shows the right items.
        Assert.Equal(1, allenVm.Transactions);
        Assert.Equal(1, ikotaVm.Transactions);
        Assert.Contains(allenVm.ByProduct, r => r.Name == pA.Name && r.Qty == 3);
        Assert.DoesNotContain(allenVm.ByProduct, r => r.Name == pB.Name);
        Assert.Contains(ikotaVm.ByProduct, r => r.Name == pB.Name && r.Qty == 2);
    }
}
