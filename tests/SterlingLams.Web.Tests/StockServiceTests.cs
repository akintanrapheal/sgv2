using Microsoft.EntityFrameworkCore;
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

    // Track Stock (and the Stock grid) apply the on-hand change through the ledger and then upsert
    // the reorder settings on the same location row. For a product with no row yet, ApplyAsync's row
    // is still a pending insert the database cannot see — looking it up with a query alone returns
    // null, a second row gets added and SaveChanges dies on the (ProductId, StoreId) unique index.
    // The callers must resolve through the change tracker first; this pins that behaviour.
    [Fact]
    public async Task Reorder_upsert_after_ApplyAsync_reuses_the_pending_row()
    {
        using var t = new TestDb();
        var store = t.SeedStore("Abuja", "Abuja", "Gwarimpa");
        var p = t.SeedProduct();          // brand-new product: no StoreInventory row anywhere

        var svc = new StockService(t.Db);
        await svc.ApplyAsync(p.Id, null, store.Id, 4, StockMovementType.Adjustment, "BSA00001");

        // A database query cannot see the row ApplyAsync just added.
        Assert.Null(await t.Db.StoreInventories
            .FirstOrDefaultAsync(si => si.ProductId == p.Id && si.StoreId == store.Id && si.ProductVariantId == null));

        var row = t.Db.StoreInventories.Local
            .FirstOrDefault(si => si.ProductId == p.Id && si.StoreId == store.Id && si.ProductVariantId == null);
        Assert.NotNull(row);
        row!.MinStock = 2;
        row.MaxStock = 20;

        await t.Db.SaveChangesAsync();

        Assert.Single(t.Db.StoreInventories.Where(si => si.ProductId == p.Id && si.StoreId == store.Id));
        var saved = t.Inv(p.Id, store.Id);
        Assert.Equal(4, saved.QuantityOnHand);
        Assert.Equal(2, saved.MinStock);
        Assert.Equal(20, saved.MaxStock);
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
