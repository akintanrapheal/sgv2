using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// One-time backfill for <see cref="OrderItem.FulfilledFromStoreId"/> (the per-item supplying branch,
/// introduced after these orders were already fulfilled). Split online orders fulfilled before the field
/// existed have it null on every line, so Finance/EOD/cash-up fall back to crediting the whole order to the
/// pickup/fulfilling branch — i.e. a split order shows as one row, not split. This reconstructs each line's
/// supplier from the order's stock transfers (the same majority rule the live fulfilment uses): a line's
/// units that came IN via a transfer are credited to the sending branch; the remainder to the fulfilling
/// branch. Runs ONCE shortly after boot and is self-guarding — it only touches online orders that have an
/// order-linked transfer and at least one still-null line, so once backfilled there is nothing left to do.
/// </summary>
public class OrderItemStoreBackfillService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<OrderItemStoreBackfillService> _logger;

    public OrderItemStoreBackfillService(IServiceProvider sp, ILogger<OrderItemStoreBackfillService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish booting (and any pending migration apply) before touching data.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (TaskCanceledException) { return; }

        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await BackfillAsync(db, _logger, stoppingToken);
        }
        catch (Exception ex) { _logger.LogError(ex, "OrderItem store-attribution backfill failed."); }
    }

    private static int Qty(StockTransferItem ti) => ti.DispatchedQty ?? ti.ApprovedQty ?? ti.RequestedQty;

    /// <summary>Does the actual work against a context — separated from the hosted-service plumbing so it
    /// can be exercised directly in tests. Returns the number of orders updated.</summary>
    internal static async Task<int> BackfillAsync(ApplicationDbContext db, ILogger logger, CancellationToken ct)
    {
        // Candidate orders: online, fulfilled (has a fulfilling branch), with an order-linked transfer and
        // at least one line whose supplier isn't set yet. (Single-branch orders with no transfer already
        // display correctly via the null fallback, so we don't churn the whole order history.)
        var orderIds = await db.Orders
            .Where(o => o.Channel == OrderChannel.Online && o.FulfillingStoreId != null
                && db.StockTransfers.Any(st => st.OrderId == o.Id)
                && o.Items.Any(i => i.FulfilledFromStoreId == null))
            .Select(o => o.Id)
            .ToListAsync(ct);

        if (orderIds.Count == 0) return 0;   // nothing to do (already backfilled)

        logger.LogInformation("OrderItem store-attribution backfill: {Count} split order(s) to process.", orderIds.Count);

        var updated = 0;
        foreach (var orderId in orderIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
                if (order?.FulfillingStoreId is not int fulfilStore) continue;

                var transfers = await db.StockTransfers.Where(t => t.OrderId == orderId)
                    .Include(t => t.Items).ToListAsync(ct);

                foreach (var line in order.Items.Where(i => i.FulfilledFromStoreId == null))
                {
                    // Units of this line that arrived from each sending branch.
                    var inboundByStore = transfers
                        .GroupBy(t => t.FromStoreId)
                        .Select(g => new
                        {
                            Store = g.Key,
                            Qty = g.SelectMany(t => t.Items)
                                   .Where(ti => ti.ProductId == line.ProductId && ti.ProductVariantId == line.ProductVariantId)
                                   .Sum(Qty)
                        })
                        .Where(x => x.Qty > 0)
                        .ToList();

                    var ownQty = line.Quantity - inboundByStore.Sum(x => x.Qty);   // supplied from the fulfilling branch's own stock

                    // Majority branch wins; the fulfilling branch wins ties (matches live fulfilment).
                    var supplier = fulfilStore;
                    var bestQty = ownQty;
                    foreach (var x in inboundByStore)
                        if (x.Qty > bestQty) { bestQty = x.Qty; supplier = x.Store; }

                    line.FulfilledFromStoreId = supplier;
                }

                await db.SaveChangesAsync(ct);
                updated++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backfill failed for order {OrderId}; skipping.", orderId);
            }
        }

        logger.LogInformation("OrderItem store-attribution backfill complete: {Updated} order(s) updated.", updated);
        return updated;
    }
}
