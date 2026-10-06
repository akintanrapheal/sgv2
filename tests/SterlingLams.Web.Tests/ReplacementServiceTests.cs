using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Website replacement: sending a different item for a bad one deducts the replacement's
/// stock now, and the returned (bad) item is resolved by Inventory into restock / write-off.</summary>
public class ReplacementServiceTests
{
    private sealed class StubAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, string? entityId, string description,
            string? changes = null, string? performedBy = null) => Task.CompletedTask;
    }

    private static ReplacementService Svc(TestDb t) =>
        new(t.Db, new StockService(t.Db), new StubAudit(), NullLogger<ReplacementService>.Instance);

    private static Order PaidOrder(TestDb t, ApplicationUser user, Store store, Product bad, int qty)
    {
        var order = t.NewDeliveryOrder(user, "Lagos", "Ajah", (bad, qty));
        order.IsPaid = true;
        order.FulfillingStoreId = store.Id;
        t.Db.SaveChanges();
        return order;
    }

    [Fact]
    public async Task Create_deducts_replacement_stock_and_queues_the_returned_item()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Ikota", "Lagos", "Ajah");
        var user = t.SeedUser();
        var bad = t.SeedProduct();
        var rep = t.SeedProduct();
        t.SetStock(rep.Id, store.Id, onHand: 5);
        var order = PaidOrder(t, user, store, bad, qty: 3);

        var res = await Svc(t).CreateAsync(order.Id, order.Items.First().Id, rep.Id, null,
            newQty: 2, reason: "tarnished", balancePaid: 0, balanceNote: null, userId: "u1", userName: "Staff");

        Assert.True(res.Success, res.Message);
        Assert.Equal(3, t.Inv(rep.Id, store.Id).QuantityOnHand);   // 5 − 2 sent out

        var row = t.Db.OrderReplacements.Single();
        Assert.Equal(RestockDecision.Pending, row.RestockDecision);
        Assert.Equal(2, row.NewQuantity);
        Assert.Equal(2, row.OldQuantity);                          // capped at the ordered qty (3) → 2
        Assert.Equal(store.Id, row.StoreId);
    }

    [Fact]
    public async Task Create_fails_when_replacement_is_out_of_stock_at_the_branch()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Allen", "Lagos", "Ikeja");
        var user = t.SeedUser();
        var bad = t.SeedProduct();
        var rep = t.SeedProduct();
        t.SetStock(rep.Id, store.Id, onHand: 1);
        var order = PaidOrder(t, user, store, bad, qty: 1);

        var res = await Svc(t).CreateAsync(order.Id, order.Items.First().Id, rep.Id, null,
            newQty: 2, reason: null, balancePaid: 0, balanceNote: null, userId: "u1", userName: "Staff");

        Assert.False(res.Success);
        Assert.Equal(1, t.Inv(rep.Id, store.Id).QuantityOnHand);   // unchanged — transaction rolled back
        Assert.Empty(t.Db.OrderReplacements);
    }

    [Fact]
    public async Task Resolve_restock_puts_the_returned_item_back()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Ikota", "Lagos", "Ajah");
        var user = t.SeedUser();
        var bad = t.SeedProduct();
        var rep = t.SeedProduct();
        t.SetStock(rep.Id, store.Id, onHand: 5);
        t.SetStock(bad.Id, store.Id, onHand: 0);
        var order = PaidOrder(t, user, store, bad, qty: 1);
        var svc = Svc(t);
        await svc.CreateAsync(order.Id, order.Items.First().Id, rep.Id, null, 1, null, 0, null, "u1", "Staff");
        var repId = t.Db.OrderReplacements.Single().Id;

        var res = await svc.ResolveStockAsync(repId, restockQty: 1, reason: null, userId: "inv1");

        Assert.True(res.Success, res.Message);
        Assert.Equal(1, t.Inv(bad.Id, store.Id).QuantityOnHand);   // bad item back on the shelf
        var row = t.Db.OrderReplacements.Single();
        Assert.Equal(RestockDecision.Restocked, row.RestockDecision);
        Assert.Equal(1, row.RestockedQuantity);
    }

    [Fact]
    public async Task Resolve_writeoff_nets_the_returned_item_to_zero_and_logs_damage()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Ikota", "Lagos", "Ajah");
        var user = t.SeedUser();
        var bad = t.SeedProduct();
        var rep = t.SeedProduct();
        t.SetStock(rep.Id, store.Id, onHand: 5);
        t.SetStock(bad.Id, store.Id, onHand: 0);
        var order = PaidOrder(t, user, store, bad, qty: 1);
        var svc = Svc(t);
        await svc.CreateAsync(order.Id, order.Items.First().Id, rep.Id, null, 1, null, 0, null, "u1", "Staff");
        var repId = t.Db.OrderReplacements.Single().Id;

        var res = await svc.ResolveStockAsync(repId, restockQty: 0, reason: "Damaged", userId: "inv1");

        Assert.True(res.Success, res.Message);
        Assert.Equal(0, t.Inv(bad.Id, store.Id).QuantityOnHand);   // in then written off → nets to zero
        Assert.True(t.Db.StockMovements.Any(m => m.ProductId == bad.Id && m.Type == StockMovementType.Damage));
        Assert.Equal(RestockDecision.WrittenOff, t.Db.OrderReplacements.Single().RestockDecision);
    }
}
