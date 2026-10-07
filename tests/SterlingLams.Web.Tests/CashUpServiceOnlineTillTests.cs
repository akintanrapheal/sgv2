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
}
