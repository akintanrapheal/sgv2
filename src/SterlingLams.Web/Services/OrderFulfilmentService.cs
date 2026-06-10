using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface IOrderFulfilmentService
{
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

    /// <summary>
    /// Picks the branch nearest the customer, transfers in any units it lacks from other
    /// branches (transfer-then-sell so every branch balance stays correct), then sells the
    /// whole order from that branch. Idempotent — does nothing if already fulfilled.
    /// Failures are logged, never thrown — the customer has already paid.
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

            // 1) Fulfilment branch: pickup → chosen store; delivery → nearest to the customer.
            var ranked = DeliveryZoneService.RankStoresByProximity(
                activeStores, order.DeliveryAddress?.State, order.DeliveryAddress?.City);
            Store fulfilStore = (order.FulfillmentType == FulfillmentType.StorePickup
                    ? activeStores.FirstOrDefault(s => s.Id == order.PickupStoreId)
                    : null)
                ?? ranked.First();

            // Order of stores to pull from: fulfilment branch first, then nearest others.
            var storeOrder = new List<int> { fulfilStore.Id };
            storeOrder.AddRange(ranked.Where(s => s.Id != fulfilStore.Id).Select(s => s.Id));

            // 2) Allocate each line across branches (StoreInventory is per-product).
            var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();
            var storeIds = activeStores.Select(s => s.Id).ToList();
            var onHand = (await _db.StoreInventories
                    .Where(si => productIds.Contains(si.ProductId) && storeIds.Contains(si.StoreId))
                    .ToListAsync())
                .ToDictionary(si => (si.ProductId, si.StoreId), si => si.QuantityOnHand);
            int OnHand(int pid, int sid) => onHand.TryGetValue((pid, sid), out var q) ? q : 0;

            // transfers[(sourceStore, product)] = qty to move into the fulfilment branch
            var transfers = new Dictionary<(int store, int product), int>();
            foreach (var line in order.Items)
            {
                var need = line.Quantity;
                foreach (var sid in storeOrder)
                {
                    if (need <= 0) break;
                    var take = Math.Min(need, OnHand(line.ProductId, sid));
                    if (take <= 0) continue;
                    onHand[(line.ProductId, sid)] = OnHand(line.ProductId, sid) - take; // reserve so repeat products don't double-allocate
                    if (sid != fulfilStore.Id)
                    {
                        transfers.TryGetValue((sid, line.ProductId), out var acc);
                        transfers[(sid, line.ProductId)] = acc + take;
                    }
                    need -= take;
                }
                // 3) Shortfall (shouldn't happen — blocked at checkout): hold for staff, don't go negative.
                if (need > 0)
                {
                    order.AdminNotes = $"Fulfilment held {DateTime.UtcNow:yyyy-MM-dd HH:mm}: insufficient stock for {line.ProductName}.";
                    await _db.SaveChangesAsync();
                    _logger.LogWarning("Order {OrderNumber} held — short {Qty} of product {ProductId}.",
                        order.OrderNumber, need, line.ProductId);
                    return;
                }
            }

            // 4) Apply: transfers (remote → fulfilment) then the sale (whole order from fulfilment).
            var products = await _db.Products.Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);
            var now = DateTime.UtcNow;
            await using var tx = await _db.Database.BeginTransactionAsync();

            foreach (var bySource in transfers.GroupBy(kv => kv.Key.store))
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
                order.OrderNumber, fulfilStore.Name, transfers.GroupBy(t => t.Key.store).Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fulfilment failed for order {OrderId}: {Message}", orderId, ex.Message);
        }
    }
}
