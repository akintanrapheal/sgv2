using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

public class StockServiceTests
{
    [Fact]
    public async Task ApplyAsync_decrements_balance_and_appends_ledger_entry()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Abuja", "Abuja", "Gwarimpa");
        var p = t.SeedProduct();
        t.SetStock(p.Id, store.Id, onHand: 10);

        var svc = new StockService(t.Db);
        var balance = await svc.ApplyAsync(p.Id, null, store.Id, -3, StockMovementType.Sale, "ORD-1");
        await t.Db.SaveChangesAsync();

        Assert.Equal(7, balance);
        Assert.Equal(7, t.Inv(p.Id, store.Id).QuantityOnHand);

        var move = t.Db.StockMovements.Single();
        Assert.Equal(StockMovementType.Sale, move.Type);
        Assert.Equal(-3, move.QuantityChange);
        Assert.Equal(7, move.BalanceAfter);
        Assert.Equal("ORD-1", move.Reference);
    }

    [Fact]
    public async Task ApplyAsync_creates_inventory_row_when_missing()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Allen", "Lagos", "Ikeja");
        var p = t.SeedProduct();

        var svc = new StockService(t.Db);
        await svc.ApplyAsync(p.Id, null, store.Id, 5, StockMovementType.Purchase, "PO-1");
        await t.Db.SaveChangesAsync();

        Assert.Equal(5, await svc.GetStockAsync(p.Id, null, store.Id));
    }

    [Fact]
    public async Task GetStockAsync_returns_zero_when_no_record()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Ikota", "Lagos", "Ajah");
        var p = t.SeedProduct();
        Assert.Equal(0, await new StockService(t.Db).GetStockAsync(p.Id, null, store.Id));
    }
}
