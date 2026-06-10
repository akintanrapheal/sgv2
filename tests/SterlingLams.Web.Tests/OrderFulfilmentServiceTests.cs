using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

public class OrderFulfilmentServiceTests
{
    private static OrderFulfilmentService Svc(TestDb t) =>
        new(t.Db, new StockService(t.Db), NullLogger<OrderFulfilmentService>.Instance);

    [Fact]
    public async Task Fulfil_consolidates_via_transfers_then_sells_from_nearest_branch()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct(price: 1000m);
        // one unit in each branch
        t.SetStock(p.Id, abuja.Id, 1);
        t.SetStock(p.Id, allen.Id, 1);
        t.SetStock(p.Id, ikota.Id, 1);

        // customer in Lagos/Ikeja buys all 3
        var order = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 3));

        await Svc(t).FulfilPaidOrderAsync(order.Id);

        // fulfilled from Allen (nearest), all branches drained to 0
        var fulfilled = await t.Db.Orders.FindAsync(order.Id);
        Assert.Equal(allen.Id, fulfilled!.FulfillingStoreId);
        Assert.Equal(OrderStatus.Processing, fulfilled.Status);
        Assert.Equal(0, t.Inv(p.Id, abuja.Id).QuantityOnHand);
        Assert.Equal(0, t.Inv(p.Id, allen.Id).QuantityOnHand);
        Assert.Equal(0, t.Inv(p.Id, ikota.Id).QuantityOnHand);

        // two transfers into Allen, both referencing the order
        var transfers = await t.Db.StockTransfers.Include(x => x.Items).ToListAsync();
        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, x => Assert.Equal(allen.Id, x.ToStoreId));
        Assert.All(transfers, x => Assert.Contains(order.OrderNumber, x.Note));

        // ledger: 1 sale (-3) + 2 transfer pairs = 5 movements
        var moves = await t.Db.StockMovements.ToListAsync();
        Assert.Equal(5, moves.Count);
        Assert.Single(moves, m => m.Type == StockMovementType.Sale && m.QuantityChange == -3 && m.StoreId == allen.Id);
        Assert.Equal(4, moves.Count(m => m.Type == StockMovementType.Transfer));
    }

    [Fact]
    public async Task Fulfil_is_idempotent()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, abuja.Id, 1);
        t.SetStock(p.Id, allen.Id, 1);
        t.SetStock(p.Id, ikota.Id, 1);
        var order = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 3));

        var svc = Svc(t);
        await svc.FulfilPaidOrderAsync(order.Id);
        await svc.FulfilPaidOrderAsync(order.Id); // second call must be a no-op

        Assert.Equal(5, await t.Db.StockMovements.CountAsync());
        Assert.Equal(0, t.Inv(p.Id, allen.Id).QuantityOnHand); // not driven negative
    }

    [Fact]
    public async Task Pickup_order_sells_from_chosen_store_without_transfers()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, ikota.Id, 5);

        var order = t.NewDeliveryOrder(user, "Lagos", "Ajah", (p, 2));
        order.FulfillmentType = FulfillmentType.StorePickup;
        order.PickupStoreId = ikota.Id;
        await t.Db.SaveChangesAsync();

        await Svc(t).FulfilPaidOrderAsync(order.Id);

        var fulfilled = await t.Db.Orders.FindAsync(order.Id);
        Assert.Equal(ikota.Id, fulfilled!.FulfillingStoreId);
        Assert.Equal(OrderStatus.ReadyForPickup, fulfilled.Status);
        Assert.Equal(3, t.Inv(p.Id, ikota.Id).QuantityOnHand);
        Assert.Empty(await t.Db.StockTransfers.ToListAsync());
    }

    [Fact]
    public async Task Reserve_holds_units_without_touching_on_hand()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, abuja.Id, 1);
        t.SetStock(p.Id, allen.Id, 1);
        t.SetStock(p.Id, ikota.Id, 1);
        var order = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 3));

        var ok = await Svc(t).TryReserveAsync(order.Id);

        Assert.True(ok);
        Assert.Equal(3, await t.Db.StockReservations.CountAsync(r => r.OrderId == order.Id));
        // on-hand untouched, reserved bumped
        foreach (var s in new[] { abuja, allen, ikota })
        {
            Assert.Equal(1, t.Inv(p.Id, s.Id).QuantityOnHand);
            Assert.Equal(1, t.Inv(p.Id, s.Id).QuantityReserved);
        }
    }

    [Fact]
    public async Task Reserve_fails_when_combined_available_is_insufficient()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, abuja.Id, 1);
        t.SetStock(p.Id, allen.Id, 1);
        t.SetStock(p.Id, ikota.Id, 1);

        var svc = Svc(t);
        // first order reserves all 3
        var first = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 3));
        Assert.True(await svc.TryReserveAsync(first.Id));

        // second order can't get even 1 — nothing available
        var second = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 1));
        Assert.False(await svc.TryReserveAsync(second.Id));
        Assert.Equal(0, await t.Db.StockReservations.CountAsync(r => r.OrderId == second.Id));
    }

    [Fact]
    public async Task Reserve_then_fulfil_releases_hold_and_sells()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, abuja.Id, 1);
        t.SetStock(p.Id, allen.Id, 1);
        t.SetStock(p.Id, ikota.Id, 1);
        var order = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 3));

        var svc = Svc(t);
        await svc.TryReserveAsync(order.Id);
        await svc.FulfilPaidOrderAsync(order.Id);

        Assert.Equal(0, await t.Db.StockReservations.CountAsync(r => r.OrderId == order.Id));
        foreach (var s in new[] { abuja, allen, ikota })
        {
            Assert.Equal(0, t.Inv(p.Id, s.Id).QuantityOnHand);
            Assert.Equal(0, t.Inv(p.Id, s.Id).QuantityReserved);
        }
    }

    [Fact]
    public async Task Release_frees_reserved_units()
    {
        using var t = new TestDb();
        var user = t.SeedUser();
        var (abuja, allen, ikota) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, allen.Id, 5);
        var order = t.NewDeliveryOrder(user, "Lagos", "Ikeja", (p, 2));

        var svc = Svc(t);
        await svc.TryReserveAsync(order.Id);
        Assert.Equal(2, t.Inv(p.Id, allen.Id).QuantityReserved);

        await svc.ReleaseReservationAsync(order.Id);
        Assert.Equal(0, t.Inv(p.Id, allen.Id).QuantityReserved);
        Assert.Equal(0, await t.Db.StockReservations.CountAsync(r => r.OrderId == order.Id));
    }
}
