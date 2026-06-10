using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface IOrderFulfilmentService
{
    /// <summary>Reserves stock for a freshly-placed (unpaid) order so concurrent orders can't
    /// oversell the same units before payment. Returns false (reserving nothing) if combined
    /// available stock can't cover the order.</summary>
    Task<bool> TryReserveAsync(int orderId);

    /// <summary>Frees an order's reservation (e.g. payment failed or the order was abandoned).</summary>
    Task ReleaseReservationAsync(int orderId);

    /// <summary>Fulfils a paid online order against the in-house stock ledger. Idempotent and
    /// safe to call from every payment-confirmation path (browser callback and webhook).</summary>
    Task FulfilPaidOrderAsync(int orderId);
}

public class OrderFulfilmentService : IOrderFulfilmentService
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly ILogger<OrderFulfilmentService> _logger;

    public OrderFulfilmentService(ApplicationDbContext db, IStockService stock,
        ILogger<OrderFulfilmentService> logger)
    {
        _db = db;
        _stock = stock;
        _logger = logger;
    }

    // ── Allocation ────────────────────────────────────────────────────────────
    // Spreads an order's lines across branches: the fulfilment branch first (pickup store, or
    // nearest to the customer), then the next-nearest. `avail(productId, storeId)` supplies the
    // usable quantity at each branch. Returns the per-(store,product) allocation and the first
    // line that couldn't be fully covered (null = success). StoreInventory is per-product.
    private static (Store fulfilStore, Dictionary<(int store, int product), int> alloc, OrderItem? shortLine)
        Allocate(Order order, List<Store> activeStores, List<Store> ranked, Func<int, int, int> avail)
    {
        Store fulfilStore = (order.FulfillmentType == FulfillmentType.StorePickup
                ? activeStores.FirstOrDefault(s => s.Id == order.PickupStoreId)
                : null)
            ?? ranked.First();

        var storeOrder = new List<int> { fulfilStore.Id };
        storeOrder.AddRange(ranked.Where(s => s.Id != fulfilStore.Id).Select(s => s.Id));

        var remaining = new Dictionary<(int product, int store), int>();
        int Remaining(int pid, int sid)
        {
            var k = (pid, sid);
            if (!remaining.TryGetValue(k, out var v)) { v = avail(pid, sid); remaining[k] = v; }
            return v;
        }

        var alloc = new Dictionary<(int store, int product), int>();
        foreach (var line in order.Items)
        {
            var need = line.Quantity;
            foreach (var sid in storeOrder)
            {
                if (need <= 0) break;
                var take = Math.Min(need, Remaining(line.ProductId, sid));
                if (take <= 0) continue;
                remaining[(line.ProductId, sid)] = Remaining(line.ProductId, sid) - take;
                alloc.TryGetValue((sid, line.ProductId), out var acc);
                alloc[(sid, line.ProductId)] = acc + take;
                need -= take;
            }
            if (need > 0) return (fulfilStore, alloc, line);
        }
        return (fulfilStore, alloc, null);
    }

    // ── Reserve / release ──────────────────────────────────────────────────────
    public async Task<bool> TryReserveAsync(int orderId)
    {
        var order = await _db.Orders.Include(o => o.Items).Include(o => o.DeliveryAddress)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null || order.Items.Count == 0) return false;

        // Idempotent: a re-submit of the same order keeps its existing hold.
        if (await _db.StockReservations.AnyAsync(r => r.OrderId == orderId)) return true;

        var activeStores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
        if (activeStores.Count == 0) return false;
        var ranked = DeliveryZoneService.RankStoresByProximity(
            activeStores, order.DeliveryAddress?.State, order.DeliveryAddress?.City);

        var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();
        var storeIds = activeStores.Select(s => s.Id).ToList();

        await using var tx = await _db.Database.BeginTransactionAsync();

        var invMap = (await _db.StoreInventories
                .Where(si => productIds.Contains(si.ProductId) && storeIds.Contains(si.StoreId))
                .ToListAsync())
            .ToDictionary(si => (si.ProductId, si.StoreId));
        int Available(int pid, int sid) =>
            invMap.TryGetValue((pid, sid), out var si) ? Math.Max(0, si.QuantityOnHand - si.QuantityReserved) : 0;

        var (_, alloc, shortLine) = Allocate(order, activeStores, ranked, Available);
        if (shortLine != null) return false; // not enough available — caller blocks checkout

        var now = DateTime.UtcNow;
        foreach (var ((sid, pid), qty) in alloc)
        {
            invMap[(pid, sid)].QuantityReserved += qty;
            _db.StockReservations.Add(new StockReservation
            {
                OrderId = orderId, StoreId = sid, ProductId = pid, Quantity = qty, CreatedAt = now
            });
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return true;
    }

    public async Task ReleaseReservationAsync(int orderId)
    {
        var rows = await _db.StockReservations.Where(r => r.OrderId == orderId).ToListAsync();
        if (rows.Count == 0) return;
        await ReleaseRowsAsync(rows);
    }

    private async Task ReleaseRowsAsync(List<StockReservation> rows)
    {
        var pids = rows.Select(r => r.ProductId).Distinct().ToList();
        var sids = rows.Select(r => r.StoreId).Distinct().ToList();
        var invMap = (await _db.StoreInventories
                .Where(si => pids.Contains(si.ProductId) && sids.Contains(si.StoreId))
                .ToListAsync())
            .ToDictionary(si => (si.ProductId, si.StoreId));
        foreach (var r in rows)
            if (invMap.TryGetValue((r.ProductId, r.StoreId), out var si))
                si.QuantityReserved = Math.Max(0, si.QuantityReserved - r.Quantity);
        _db.StockReservations.RemoveRange(rows);
        await _db.SaveChangesAsync();
    }

    // ── Fulfil ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Picks the branch nearest the customer, transfers in any units it lacks from other
    /// branches (transfer-then-sell so every branch balance stays correct), then sells the
    /// whole order from that branch — releasing this order's own reservation as it goes.
    /// Idempotent. Failures are logged, never thrown — the customer has already paid.
    /// </summary>
    public async Task FulfilPaidOrderAsync(int orderId)
    {
        try
        {
            var order = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.PickupStore)
                .Include(o => o.DeliveryAddress)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return;

            // Idempotency: a fulfilled order already has its branch + ledger movements.
            if (order.FulfillingStoreId != null) return;

            var activeStores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
            if (activeStores.Count == 0)
            {
                _logger.LogError("No active stores — cannot fulfil order {OrderNumber}.", order.OrderNumber);
                return;
            }
            var ranked = DeliveryZoneService.RankStoresByProximity(
                activeStores, order.DeliveryAddress?.State, order.DeliveryAddress?.City);

            var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();
            var storeIds = activeStores.Select(s => s.Id).ToList();
            var invMap = (await _db.StoreInventories
                    .Where(si => productIds.Contains(si.ProductId) && storeIds.Contains(si.StoreId))
                    .ToListAsync())
                .ToDictionary(si => (si.ProductId, si.StoreId));

            // This order's own reservation — added back into availability so the sale draws on the
            // units we already held (other orders' reservations stay off-limits).
            var resRows = await _db.StockReservations.Where(r => r.OrderId == orderId).ToListAsync();
            var ownRes = new Dictionary<(int product, int store), int>();
            foreach (var r in resRows)
            {
                ownRes.TryGetValue((r.ProductId, r.StoreId), out var a);
                ownRes[(r.ProductId, r.StoreId)] = a + r.Quantity;
            }
            int SaleAvail(int pid, int sid)
            {
                if (!invMap.TryGetValue((pid, sid), out var si)) return 0;
                ownRes.TryGetValue((pid, sid), out var mine);
                return Math.Max(0, si.QuantityOnHand - si.QuantityReserved + mine);
            }

            var (fulfilStore, alloc, shortLine) = Allocate(order, activeStores, ranked, SaleAvail);
            if (shortLine != null)
            {
                order.AdminNotes = $"Fulfilment held {DateTime.UtcNow:yyyy-MM-dd HH:mm}: insufficient stock for {shortLine.ProductName}.";
                await _db.SaveChangesAsync();
                _logger.LogWarning("Order {OrderNumber} held — insufficient stock for product {ProductId}.",
                    order.OrderNumber, shortLine.ProductId);
                return;
            }

            var products = await _db.Products.Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);
            var now = DateTime.UtcNow;
            await using var tx = await _db.Database.BeginTransactionAsync();

            // Free this order's hold (units are now being sold, not just reserved).
            if (resRows.Count > 0) await ReleaseRowsAsync(resRows);

            // Transfers: every allocation that isn't already at the fulfilment branch moves in.
            foreach (var bySource in alloc.Where(kv => kv.Key.store != fulfilStore.Id).GroupBy(kv => kv.Key.store))
            {
                var sourceStore = activeStores.First(s => s.Id == bySource.Key);
                var transferNumber = $"TRF-{now:yyMMdd}-{now:HHmmssfff}-{sourceStore.Id}";
                var transfer = new StockTransfer
                {
                    TransferNumber = transferNumber,
                    FromStoreId = sourceStore.Id,
                    ToStoreId = fulfilStore.Id,
                    CreatedByUserId = order.UserId,
                    Note = $"Online order {order.OrderNumber}",
                    CreatedAt = now
                };
                foreach (var kv in bySource)
                {
                    var pid = kv.Key.product;
                    var qty = kv.Value;
                    transfer.Items.Add(new StockTransferItem
                    {
                        ProductId = pid,
                        ProductName = products.GetValueOrDefault(pid, $"#{pid}"),
                        Quantity = qty
                    });
                    await _stock.ApplyAsync(pid, null, sourceStore.Id, -qty, StockMovementType.Transfer,
                        transferNumber, $"To {fulfilStore.Name} (order {order.OrderNumber})", order.UserId);
                    await _stock.ApplyAsync(pid, null, fulfilStore.Id, qty, StockMovementType.Transfer,
                        transferNumber, $"From {sourceStore.Name} (order {order.OrderNumber})", order.UserId);
                }
                _db.StockTransfers.Add(transfer);
            }

            // Sell the whole order from the fulfilment branch (everything is consolidated there now).
            foreach (var line in order.Items)
                await _stock.ApplyAsync(line.ProductId, line.ProductVariantId, fulfilStore.Id,
                    -line.Quantity, StockMovementType.Sale, order.OrderNumber,
                    $"Online order {order.OrderNumber}", order.UserId);

            order.FulfillingStoreId = fulfilStore.Id;
            order.Status = order.FulfillmentType == FulfillmentType.StorePickup
                ? OrderStatus.ReadyForPickup
                : OrderStatus.Processing;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            _logger.LogInformation(
                "Order {OrderNumber} fulfilled from {Store} ({Transfers} transfer(s)).",
                order.OrderNumber, fulfilStore.Name,
                alloc.Count(kv => kv.Key.store != fulfilStore.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fulfilment failed for order {OrderId}: {Message}", orderId, ex.Message);
        }
    }
}
