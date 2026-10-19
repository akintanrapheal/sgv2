using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Inter-branch transfer workflow: Request → Approve → Dispatch → Receive moves stock
/// from the source branch to the destination and records it in the ledger.</summary>
public class TransferWorkflowServiceTests
{
    // Manual transfers aren't tied to an online order, so the fulfilment hook is never invoked.
    private sealed class StubFulfilment : IOrderFulfilmentService
    {
        public Task ReleaseReservationAsync(int orderId) => Task.CompletedTask;
        public Task<FulfilOutcome> FulfilPaidOrderAsync(int orderId, bool refundOnSoldOut = true) => Task.FromResult(FulfilOutcome.Fulfilled);
        public Task FinalizeAwaitingOrderAsync(int orderId) => Task.CompletedTask;
        public Task NotifyLogisticsDispatchAsync(int orderId, string? orderUrl) => Task.CompletedTask;
    }

    [Fact]
    public async Task Full_transfer_moves_stock_from_source_to_destination()
    {
        using var t = new TestDb();
        var (abuja, allen, _) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, abuja.Id, onHand: 10);
        t.SetStock(p.Id, allen.Id, onHand: 0);

        var svc = new TransferWorkflowService(t.Db, new StockService(t.Db), new StubFulfilment());

        var req = new TransferRequest
        {
            FromStoreId = abuja.Id,
            ToStoreId = allen.Id,
            Items = { new TransferLine { ProductId = p.Id, Quantity = 4 } }
        };
        var (ok, err, id) = await svc.RequestAsync(req, "staff-1");
        Assert.True(ok, err);
        Assert.NotNull(id);

        var item = await t.Db.StockTransferItems.AsNoTracking().FirstAsync(i => i.StockTransferId == id!.Value);

        Assert.True((await svc.ApproveAsync(id!.Value, new() { new ItemQtyDto(item.Id, 4) }, "admin-1")).Success);
        Assert.True((await svc.DispatchAsync(id.Value, new() { new ItemQtyDto(item.Id, 4) }, null, null, null, "staff-1")).Success);
        Assert.True((await svc.ReceiveAsync(id.Value, new() { new ReceiveLineDto(item.Id, 4, 0, 0) }, null, "staff-2")).Success);

        // Stock moved: source 10 → 6, destination 0 → 4.
        Assert.Equal(6, t.Inv(p.Id, abuja.Id).QuantityOnHand);
        Assert.Equal(4, t.Inv(p.Id, allen.Id).QuantityOnHand);

        // And it's traceable in the ledger (a Transfer movement out of the source).
        Assert.Contains(t.Db.StockMovements,
            m => m.ProductId == p.Id && m.StoreId == abuja.Id && m.Type == StockMovementType.Transfer && m.QuantityChange < 0);
    }

    // Drives a transfer of `qty` to PartiallyReceived by receiving only `receive` of them.
    private static async Task<(int id, int itemId, Store abuja, Store allen, Product p)> PartialAsync(TestDb t, int qty, int receive)
    {
        var (abuja, allen, _) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, abuja.Id, onHand: 10);
        t.SetStock(p.Id, allen.Id, onHand: 0);
        var svc = new TransferWorkflowService(t.Db, new StockService(t.Db), new StubFulfilment());
        var (ok, err, id) = await svc.RequestAsync(new TransferRequest
        {
            FromStoreId = abuja.Id, ToStoreId = allen.Id,
            Items = { new TransferLine { ProductId = p.Id, Quantity = qty } }
        }, "staff-1");
        Assert.True(ok, err);
        var item = await t.Db.StockTransferItems.AsNoTracking().FirstAsync(i => i.StockTransferId == id!.Value);
        await svc.ApproveAsync(id!.Value, new() { new ItemQtyDto(item.Id, qty) }, "admin-1");
        await svc.DispatchAsync(id.Value, new() { new ItemQtyDto(item.Id, qty) }, null, null, null, "staff-1");
        await svc.ReceiveAsync(id.Value, new() { new ReceiveLineDto(item.Id, receive, 0, 0) }, null, "staff-2");
        Assert.Equal(TransferStatus.PartiallyReceived, (await t.Db.StockTransfers.AsNoTracking().FirstAsync(x => x.Id == id.Value)).Status);
        return (id.Value, item.Id, abuja, allen, p);
    }

    [Fact]
    public async Task Closing_partial_with_return_to_origin_puts_pending_stock_back_at_the_source()
    {
        using var t = new TestDb();
        var (id, itemId, abuja, allen, p) = await PartialAsync(t, qty: 4, receive: 2);  // abuja 10→6, allen 0→2, 2 pending
        var svc = new TransferWorkflowService(t.Db, new StockService(t.Db), new StubFulfilment());

        var r = await svc.CompleteAsync(id, "return", "driver brought 2 back", "staff-2");

        Assert.True(r.Success, r.Error);
        Assert.Equal(TransferStatus.Completed, (await t.Db.StockTransfers.AsNoTracking().FirstAsync(x => x.Id == id)).Status);
        Assert.Equal(8, t.Inv(p.Id, abuja.Id).QuantityOnHand);   // 6 + 2 returned
        Assert.Equal(2, t.Inv(p.Id, allen.Id).QuantityOnHand);   // unchanged
        var item = await t.Db.StockTransferItems.AsNoTracking().FirstAsync(i => i.Id == itemId);
        Assert.Equal(2, item.ReturnedQty);
        Assert.Equal(0, item.PendingQty);
    }

    [Fact]
    public async Task Closing_partial_with_write_off_restores_no_stock_and_records_the_loss()
    {
        using var t = new TestDb();
        var (id, itemId, abuja, allen, p) = await PartialAsync(t, qty: 4, receive: 2);
        var svc = new TransferWorkflowService(t.Db, new StockService(t.Db), new StubFulfilment());

        var r = await svc.CompleteAsync(id, "writeoff", "lost in transit", "staff-2");

        Assert.True(r.Success, r.Error);
        Assert.Equal(6, t.Inv(p.Id, abuja.Id).QuantityOnHand);   // NOT restored — the loss stands
        var item = await t.Db.StockTransferItems.AsNoTracking().FirstAsync(i => i.Id == itemId);
        Assert.Equal(2, item.WontFulfilQty);
        Assert.Equal(0, item.PendingQty);
    }

    [Fact]
    public async Task Closing_a_partial_transfer_requires_a_reason()
    {
        using var t = new TestDb();
        var (id, _, _, _, _) = await PartialAsync(t, qty: 4, receive: 2);
        var svc = new TransferWorkflowService(t.Db, new StockService(t.Db), new StubFulfilment());

        var r = await svc.CompleteAsync(id, "return", "   ", "staff-2");

        Assert.False(r.Success);
        Assert.Equal(TransferStatus.PartiallyReceived, (await t.Db.StockTransfers.AsNoTracking().FirstAsync(x => x.Id == id)).Status);
    }

    [Fact]
    public async Task Dispatching_more_than_the_branch_holds_fails_cleanly_instead_of_throwing()
    {
        using var t = new TestDb();
        var (abuja, allen, _) = t.SeedBranches();
        var p = t.SeedProduct();
        t.SetStock(p.Id, abuja.Id, onHand: 5);

        var svc = new TransferWorkflowService(t.Db, new StockService(t.Db), new StubFulfilment());

        var req = new TransferRequest
        {
            FromStoreId = abuja.Id,
            ToStoreId = allen.Id,
            Items = { new TransferLine { ProductId = p.Id, Quantity = 5 } }
        };
        var (ok, err, id) = await svc.RequestAsync(req, "staff-1");
        Assert.True(ok, err);
        var item = await t.Db.StockTransferItems.AsNoTracking().FirstAsync(i => i.StockTransferId == id!.Value);
        Assert.True((await svc.ApproveAsync(id!.Value, new() { new ItemQtyDto(item.Id, 5) }, "admin-1")).Success);

        // Approval reserves the units, but a write-off adjusts on-hand without consulting
        // reservations (deliberate — a POS sale does the same), so on-hand can drop below what was
        // approved. Dispatching then asks for more than the branch actually holds.
        await new StockService(t.Db).ApplyAsync(p.Id, null, abuja.Id, -3,
            StockMovementType.Adjustment, "damage");
        await t.Db.SaveChangesAsync();

        var result = await svc.DispatchAsync(id.Value, new() { new ItemQtyDto(item.Id, 5) }, null, null, null, "staff-1");

        // A message, not an exception — this used to escape as an error page because the catch sat
        // around SaveChanges while the stock service throws from ApplyAsync itself.
        Assert.False(result.Success);
        Assert.Contains("only has 2", result.Error);

        // And nothing was half-applied: the transfer is still awaiting dispatch.
        var after = await t.Db.StockTransfers.AsNoTracking().FirstAsync(x => x.Id == id.Value);
        Assert.Equal(TransferStatus.Approved, after.Status);
    }
}
