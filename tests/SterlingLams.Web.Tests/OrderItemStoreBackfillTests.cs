using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SterlingLams.Web.Infrastructure;
using SterlingLams.Web.Models.Domain;
using Xunit;

namespace SterlingLams.Web.Tests;

/// <summary>Split online orders fulfilled before OrderItem.FulfilledFromStoreId existed have it null on
/// every line. The one-time backfill reconstructs each line's supplier from the order's stock transfers:
/// units that arrived via a transfer are credited to the sending branch, the rest to the fulfilling branch.</summary>
public class OrderItemStoreBackfillTests
{
    [Fact]
    public async Task Backfills_supplier_from_transfers_and_leaves_own_stock_with_the_fulfilling_branch()
    {
        using var t = new TestDb();
        var (_, allen, ikota) = t.SeedBranches();
        var buyer = t.SeedUser();
        var ikotaLine = t.SeedProduct(23900m);   // Ikota fulfils from own stock
        var allenLine = t.SeedProduct(9950m);    // packed at Allen, sent to Ikota to merge

        // Fulfilled by Ikota; both lines have NO per-item store (legacy).
        var order = new Order
        {
            OrderNumber = "SGW-LEGACY-SPLIT", UserId = buyer.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.StorePickup, PickupStoreId = ikota.Id, FulfillingStoreId = ikota.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = DateTime.UtcNow, PackedAt = DateTime.UtcNow,
            Currency = "NGN", Subtotal = 33850m, Total = 33850m,
            Items =
            {
                new OrderItem { ProductId = ikotaLine.Id, ProductName = ikotaLine.Name, Quantity = 1, UnitPrice = 23900m },
                new OrderItem { ProductId = allenLine.Id, ProductName = allenLine.Name, Quantity = 1, UnitPrice = 9950m },
            }
        };
        t.Db.Orders.Add(order);
        await t.Db.SaveChangesAsync();

        // Allen shipped its line to Ikota to merge.
        t.Db.StockTransfers.Add(new StockTransfer
        {
            TransferNumber = "TRF-BF", FromStoreId = allen.Id, ToStoreId = ikota.Id, OrderId = order.Id,
            Status = TransferStatus.Completed, DispatchedAt = DateTime.UtcNow,
            Items = { new StockTransferItem { ProductId = allenLine.Id, ProductName = allenLine.Name, RequestedQty = 1, ApprovedQty = 1, DispatchedQty = 1 } }
        });
        await t.Db.SaveChangesAsync();

        var updated = await OrderItemStoreBackfillService.BackfillAsync(t.Db, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, updated);
        var items = t.Db.Orders.Single().Items;
        Assert.Equal(allen.Id, items.Single(i => i.ProductId == allenLine.Id).FulfilledFromStoreId);   // from the transfer
        Assert.Equal(ikota.Id, items.Single(i => i.ProductId == ikotaLine.Id).FulfilledFromStoreId);   // own stock

        // Idempotent: a second run finds nothing to do.
        Assert.Equal(0, await OrderItemStoreBackfillService.BackfillAsync(t.Db, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task Leaves_single_branch_orders_without_transfers_untouched()
    {
        using var t = new TestDb();
        var (_, allen, _) = t.SeedBranches();
        var buyer = t.SeedUser();
        var p = t.SeedProduct(5000m);

        t.Db.Orders.Add(new Order
        {
            OrderNumber = "SGW-SOLO", UserId = buyer.Id, Channel = OrderChannel.Online,
            FulfillmentType = FulfillmentType.Delivery, FulfillingStoreId = allen.Id,
            Status = OrderStatus.Processing, IsPaid = true, PaidAt = DateTime.UtcNow, PackedAt = DateTime.UtcNow,
            Currency = "NGN", Subtotal = 5000m, Total = 5000m,
            Items = { new OrderItem { ProductId = p.Id, ProductName = p.Name, Quantity = 1, UnitPrice = 5000m } }
        });
        await t.Db.SaveChangesAsync();

        var updated = await OrderItemStoreBackfillService.BackfillAsync(t.Db, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, updated);   // no transfer → not a candidate; the null fallback already credits Allen
        Assert.Null(t.Db.Orders.Single().Items.Single().FulfilledFromStoreId);
    }
}
